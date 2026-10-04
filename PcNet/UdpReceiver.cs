using System.Net;
using System.Net.Sockets;

namespace PcNet;

/// <summary>CUM1 UDP 即時影像接收：重組＋抖動緩衝＋NACK。
/// 格式：[MAGIC 0x43554D31][seq u32][tsUs u64][flags 1B][fragIdx u16][fragTotal u16][payload≤1400B]。
/// 按 seq 排序輸出，完整幀等 60ms 放行；缺口 120ms 批量 NACK；單洞最多擋 150ms；
/// 500ms 沒齊丟棄記 lost。下游 AVCC 位元組跟 TCP 路徑一致。
/// v1 差異：砍 TakeSecondStats／視窗計數（ABR 回授已隨 ABR 整組刪除）。</summary>
public sealed class UdpReceiver : IDisposable
{
    public const uint Magic = 0x43554D31;
    public const int HeaderSize = 21;
    public const int DefaultPort = 19302;
    public const int HoldMs = 60;
    public const int NackMs = 120;
    public const int SkipMs = 150;
    public const int DropMs = 500;

    public sealed record VideoFrame(byte[] Avcc, bool Key, long TsUs, int Seq);

    public event Action<VideoFrame>? FrameReady;
    public event Action<int[]>? NackNeeded;

    private UdpClient? _udp;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private readonly object _lock = new();
    private readonly Dictionary<int, FragBuf> _partial = new();
    private readonly SortedDictionary<int, ReadyFrame> _jitter = new();
    private int _nextOut = -1;
    private long _lastNackMs;
    private long _lastArrivalMs = -1;
    private long _lastArrivalTs;
    private readonly HashSet<int> _nackPend = new();
    private readonly Dictionary<int, long> _nextOutDropAt = new();

    public long Received { get; private set; }
    public long Complete { get; private set; }
    public long Lost { get; private set; }
    public long Duplicates { get; private set; }
    public double JitterMs { get; private set; }

    private sealed class FragBuf
    {
        public byte[][] Frags = Array.Empty<byte[]>();
        public int Got;
        public int Total;
        public bool Key;
        public long TsUs;
        public long FirstMs;
    }

    private sealed record ReadyFrame(byte[] Avcc, bool Key, long TsUs, int Seq, long ReadyMs);

    public int Port { get; }

    public UdpReceiver(int port = DefaultPort) { Port = port; }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        try { _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        lock (_lock)
        {
            _partial.Clear();
            _jitter.Clear();
            _nextOut = -1;
        }
        _thread = new Thread(() => RecvLoop(_cts.Token)) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts?.Dispose(); } catch { }
    }

    private void RecvLoop(CancellationToken ct)
    {
        var udp = _udp;
        if (udp == null) return;
        IPEndPoint remote = new(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            byte[] dg;
            try { dg = udp.Receive(ref remote); }
            catch { return; }
            try { OnDatagram(dg); } catch { }
        }
    }

    private void OnDatagram(byte[] dg)
    {
        if (dg.Length < HeaderSize + 1) return;
        uint magic = (uint)((dg[0] << 24) | (dg[1] << 16) | (dg[2] << 8) | dg[3]);
        if (magic != Magic) return;
        int seq = (dg[4] << 24) | (dg[5] << 16) | (dg[6] << 8) | dg[7];
        long ts = 0;
        for (int i = 0; i < 8; i++) ts = (ts << 8) | dg[8 + i];
        byte flags = dg[16];
        int fragIdx = (dg[17] << 8) | dg[18];
        int fragTotal = (dg[19] << 8) | dg[20];
        if (fragTotal <= 0 || fragTotal > 600 || fragIdx < 0 || fragIdx >= fragTotal) return;
        int payLen = dg.Length - HeaderSize;
        long nowMs = Environment.TickCount64;
        List<int>? nack = null;
        List<VideoFrame>? ready = null;
        lock (_lock)
        {
            Received++;
            if (_lastArrivalMs >= 0)
            {
                long arrDiff = nowMs - _lastArrivalMs;
                double tsDiffMs = (ts - _lastArrivalTs) / 1000.0;
                double d = Math.Abs(arrDiff - tsDiffMs);
                JitterMs += (d - JitterMs) / 16.0;
            }
            _lastArrivalMs = nowMs;
            _lastArrivalTs = ts;
            if (!_partial.TryGetValue(seq, out var fb))
            {
                fb = new FragBuf
                {
                    Frags = new byte[fragTotal][],
                    Total = fragTotal,
                    Key = (flags & 1) != 0,
                    TsUs = ts,
                    FirstMs = nowMs,
                };
                _partial[seq] = fb;
            }
            else if (fb.Total != fragTotal)
            {
                fb.Frags = new byte[fragTotal][];
                fb.Got = 0;
                fb.Total = fragTotal;
                fb.FirstMs = nowMs;
            }
            if (fb.Frags[fragIdx] == null)
            {
                var part = new byte[payLen];
                Buffer.BlockCopy(dg, HeaderSize, part, 0, payLen);
                fb.Frags[fragIdx] = part;
                fb.Got++;
            }
            else Duplicates++;
            if (fb.Got >= fb.Total)
            {
                int total = 0;
                foreach (var f in fb.Frags) total += f.Length;
                var avcc = new byte[total];
                int off = 0;
                foreach (var f in fb.Frags) { Buffer.BlockCopy(f, 0, avcc, off, f.Length); off += f.Length; }
                _partial.Remove(seq);
                Complete++;
                if (_nextOut < 0 && fb.Key) _nextOut = seq; // 從 keyframe 對齊
                _jitter[seq] = new ReadyFrame(avcc, fb.Key, ts, seq, nowMs);
            }
            ready = new List<VideoFrame>();
            while (true)
            {
                if (_nextOut < 0)
                {
                    int? best = null;
                    foreach (var kv in _jitter)
                    {
                        if (kv.Value.Key) { best = kv.Key; break; }
                    }
                    if (best == null)
                    {
                        var drop = new List<int>();
                        foreach (var kv in _jitter)
                            if (nowMs - kv.Value.ReadyMs > DropMs) drop.Add(kv.Key);
                        foreach (int s in drop) { _jitter.Remove(s); Lost++; }
                        break;
                    }
                    _nextOut = best.Value;
                }
                if (!_jitter.TryGetValue(_nextOut, out var rf)) break;
                if (nowMs - rf.ReadyMs < HoldMs) break;
                _jitter.Remove(_nextOut);
                ready.Add(new VideoFrame(rf.Avcc, rf.Key, rf.TsUs, rf.Seq));
                _nextOut++;
            }
            if (_nextOut >= 0 && !_jitter.ContainsKey(_nextOut))
            {
                bool assembling = _partial.TryGetValue(_nextOut, out var pfb)
                    && nowMs - pfb.FirstMs < DropMs;
                if (!assembling)
                {
                    _nackPend.Add(_nextOut);
                    if (nowMs - _lastNackMs > 200 && _nackPend.Count > 0)
                    {
                        _lastNackMs = nowMs;
                        nack = new List<int>(_nackPend);
                        _nackPend.Clear();
                    }
                    if (!_nextOutDropAt.TryGetValue(_nextOut, out var t0))
                        _nextOutDropAt[_nextOut] = nowMs;
                    else if (nowMs - t0 > SkipMs)
                    {
                        _nextOutDropAt.Remove(_nextOut);
                        _nextOut++;
                        Lost++;
                    }
                }
            }
            else
            {
                _nextOutDropAt.Remove(_nextOut);
            }
            if (_partial.Count > 128)
            {
                var drop = new List<int>();
                foreach (var kv in _partial)
                    if (nowMs - kv.Value.FirstMs > DropMs) drop.Add(kv.Key);
                foreach (int s in drop) { _partial.Remove(s); Lost++; }
            }
            {
                var drop = new List<int>();
                foreach (var kv in _jitter)
                    if (kv.Key < _nextOut || nowMs - kv.Value.ReadyMs > DropMs) drop.Add(kv.Key);
                foreach (int s in drop) { _jitter.Remove(s); Lost++; }
                if (_jitter.Count > 256)
                {
                    var keys = new List<int>(_jitter.Keys);
                    for (int i = 0; i < keys.Count - 64; i++)
                    {
                        _jitter.Remove(keys[i]);
                        Lost++;
                    }
                }
            }
        }
        if (ready != null)
            foreach (var f in ready)
                try { FrameReady?.Invoke(f); } catch { }
        if (nack != null && nack.Count > 0)
            try { NackNeeded?.Invoke(nack.ToArray()); } catch { }
    }
}
