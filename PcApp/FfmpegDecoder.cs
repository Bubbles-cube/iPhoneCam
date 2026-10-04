using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO; // 本專案 ImplicitUsings 沒含 System.IO（AGENTS 已記載，缺就手動加）

namespace PcApp;

/// <summary>ffmpeg 子程序解碼：stdin 餵 AnnexB H.264，stdout 讀 NV12。
/// v1 驗證過的邏輯原樣保留：有界餵幀佇列（滿了丟最新不擋網路）＋三槽 ring（免 Clone）
/// ＋死亡原因＋probe 耐心。v2 差異：無 eq 濾鏡、H.264 only。
/// 禁止加 -fflags nobuffer（v1 實測它讓解碼 2 幀就停住）。</summary>
public sealed class FfmpegDecoder : IDisposable
{
    private Process? _proc;
    private Thread? _reader, _writer;
    private CancellationTokenSource? _cts;
    private BlockingCollection<byte[]>? _feedQ;
    public long FeedDrops { get; private set; }
    private long _wroteBytes, _wroteN;
    public string LastFeedLog { get; private set; } = "";
    private readonly object _frameLock = new();
    private byte[][] _ring = Array.Empty<byte[]>();
    private int _prodSlot;
    private byte[]? _latest;
    private int _latestSlot = -1, _heldSlot = -1;
    private bool _disposed;

    /// <summary>手機 rot→ffmpeg 濾鏡（先轉正再鏡像；前鏡 hflip 放 transpose 前會差 180°）。</summary>
    public static string BuildFilter(int rot, bool flip)
    {
        string t = rot switch
        {
            90 => "transpose=1",
            180 => "transpose=1,transpose=1",
            270 => "transpose=2",
            _ => "",
        };
        if (!flip) return t;
        return t.Length > 0 ? t + ",hflip" : "hflip";
    }

    public bool Running => _proc != null && !_proc.HasExited;
    public string LastError { get; private set; } = "";
    public int ShownCount { get; private set; }
    public long FramesRead { get; private set; }
    public TimeSpan CpuTime
    {
        get { try { return _proc?.TotalProcessorTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
    }
    public string DeathReason { get; private set; } = "";
    public List<string> ErrTail { get; } = new();
    public string LastErrLines(int n)
    {
        lock (ErrTail)
        {
            if (ErrTail.Count == 0) return "";
            int from = Math.Max(0, ErrTail.Count - n);
            return string.Join(" / ", ErrTail.GetRange(from, ErrTail.Count - from));
        }
    }
    public string PipeState()
    {
        try
        {
            return $"reader={(_reader?.IsAlive == true ? "活" : "死")} writer={(_writer?.IsAlive == true ? "活" : "死")}" +
                $" q={_feedQ?.Count ?? -1} lastErr=[{LastError}]";
        }
        catch { return "?"; }
    }
    public string HwMode { get; private set; } = "";
    public long UpMs { get { try { return _proc != null ? Environment.TickCount64 - _startMs : 0; } catch { return 0; } } }
    private long _startMs;
    public int OutW { get; private set; }
    public int OutH { get; private set; }

    public static string? FindFfmpeg()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(@"C:\Temp\ffmpeg", "ffmpeg-*"))
            {
                string p = Path.Combine(dir, "bin", "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    public bool Start(int width, int height, int fps, string ffmpegPath, int rot = 0, bool flip = false, bool hw = true)
    {
        Stop();
        if (width <= 0 || height <= 0) return false;
        try
        {
            string vf = BuildFilter(rot, flip);
            string vffull = "hwdownload,format=nv12" + (vf.Length > 0 ? "," + vf : "");
            if (!hw) vffull = "format=nv12" + (vf.Length > 0 ? "," + vf : "");
            HwMode = hw ? "硬解" : "軟解";
            _startMs = Environment.TickCount64;
            if (rot % 180 == 0) { OutW = width; OutH = height; }
            else { OutW = height; OutH = width; }
            DeathReason = "";
            lock (ErrTail) { ErrTail.Clear(); }
            var psi = new ProcessStartInfo(ffmpegPath,
                $"-hide_banner -loglevel error -probesize 500000 -analyzeduration 1000000 " +
                (hw ? "-hwaccel d3d11va -hwaccel_output_format d3d11 " : "") +
                $"-f h264 -framerate {fps} -i pipe:0" +
                $" -vf {vffull}" +
                $" -f rawvideo -pix_fmt nv12 pipe:1")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);
            if (_proc == null) { LastError = "ffmpeg 起不來"; return false; }
            var proc = _proc;
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (ErrTail)
                {
                    ErrTail.Add(e.Data);
                    while (ErrTail.Count > 30) ErrTail.RemoveAt(0);
                }
            };
            proc.BeginErrorReadLine();
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                string tail;
                lock (ErrTail) { tail = ErrTail.Count > 0 ? ErrTail[^1] : "(無 stderr)"; }
                try { DeathReason = $"exit {proc.ExitCode}：{tail}"; } catch { }
            };
            _cts = new CancellationTokenSource();
            FeedDrops = 0;
            _feedQ = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 4);
            _writer = new Thread(() => WriteLoop(_cts.Token)) { IsBackground = true };
            _writer.Start();
            try
            {
                int size = OutW * OutH * 3 / 2;
                _ring = [new byte[size], new byte[size], new byte[size]];
                _prodSlot = 0; _latest = null; _latestSlot = -1; _heldSlot = -1;
            }
            catch { _ring = Array.Empty<byte[]>(); }
            _reader = new Thread(() => ReadLoop(_cts.Token)) { IsBackground = true };
            _reader.Start();
            return true;
        }
        catch (Exception e) { LastError = "start: " + e.Message; return false; }
    }

    public void Feed(byte[] annexb)
    {
        try
        {
            var q = _feedQ;
            if (q == null || q.IsAddingCompleted) return;
            if (!q.TryAdd(annexb)) FeedDrops++;
        }
        catch { }
    }

    private void WriteLoop(CancellationToken ct)
    {
        try
        {
            var stdin = _proc?.StandardInput.BaseStream;
            var q = _feedQ;
            if (stdin == null || q == null) return;
            while (!ct.IsCancellationRequested)
            {
                byte[] b;
                try { b = q.Take(ct); }
                catch (OperationCanceledException) { return; }
                catch (InvalidOperationException) { return; }
                try { stdin.Write(b, 0, b.Length); _wroteBytes += b.Length; _wroteN++; }
                catch (Exception e) { LastError = "feed: " + e.Message; return; }
                if (_wroteN == 1 || _wroteN % 150 == 0) LastFeedLog = $"wrote {_wroteN}包 {_wroteBytes}B";
            }
        }
        catch (Exception e) { LastError = "feedloop: " + e.Message; }
    }

    public byte[]? TakeLatest()
    {
        lock (_frameLock)
        {
            var f = _latest;
            _heldSlot = _latestSlot;
            _latest = null;
            if (f != null) ShownCount++;
            return f;
        }
    }

    private void ReadLoop(CancellationToken ct)
    {
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            int size = OutW * OutH * 3 / 2;
            while (!ct.IsCancellationRequested)
            {
                int s, held;
                lock (_frameLock) { held = _heldSlot; s = _prodSlot % 3; if (s == held) s = (s + 1) % 3; if (s == held) s = (s + 1) % 3; }
                if (_ring.Length != 3 || _ring[s].Length != size) return;
                byte[] buf = _ring[s];
                int got = 0;
                while (got < size)
                {
                    int n = stdout.Read(buf, got, size - got);
                    if (n == 0) return;
                    got += n;
                }
                FramesRead++;
                lock (_frameLock) { _latest = buf; _latestSlot = s; _prodSlot = s + 1; }
            }
        }
        catch (Exception e) { LastError = "read: " + e.Message; }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _feedQ?.CompleteAdding(); } catch { }
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.StandardInput.Close(); } catch { }
                if (!_proc.WaitForExit(500)) try { _proc.Kill(); } catch { }
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        _ring = Array.Empty<byte[]>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
