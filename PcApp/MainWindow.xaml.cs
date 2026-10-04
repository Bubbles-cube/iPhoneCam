using System.Text;
using System.Windows;
using DirectN;
using Windows.Devices.Enumeration;
using Microsoft.Win32;
using PcNet;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Diagnostics;

namespace PcApp;

/// <summary>v2 PC 主程式：精簡重寫。無 eq 濾鏡、無美顏、無 ABR 回授。
/// v1 教訓內建：網路迴圈跑背景／餵幀有界佇列／Invoke 移出鎖／斷線清殘留／信標不洗接收狀態。</summary>
public partial class MainWindow : Window
{
    private const string FriendlyName = "iPhoneCam";
    private const string SourceId = "{8136b935-e87f-4c31-9456-31eedfd84776}"; // 沿用 v1（OBS 已認得）
    private IComObject<IMFVirtualCamera>? _camera;
    private bool _mfStarted;
    private int _camW = 960, _camH = 720; // v2 預設 720P（4:3 原生）

    private static bool IsCamTier(int w, int h) =>
        (w == 960 && h == 720) || (w == 1440 && h == 1080) ||
        (w == 720 && h == 960) || (w == 1080 && h == 1440);

    private readonly object _lock = new();
    private BeaconListener? _beacon;
    private CamClient? _client;
    private UdpReceiver? _udp;
    private bool _useUdp;
    private bool _connected;
    private long _connMs;
    private FileStream? _dump;
    private bool _saving;

    private readonly FfmpegDecoder _ff = new();
    private readonly SharedFrameWriter _frameWriter = new();
    private int _vidW, _vidH, _vidFps = 30, _vidRot;
    private bool _vidFlip;
    private string _ffKey = "", _ffBase = "", _previewLabel = "";
    private long _lastFfStartMs, _lastNetLogMs, _lastShownLogMs;
    private long _shown, _drops, _netIn;
    private string _dropLoggedFor = "", _firstLoggedFor = "";
    private bool _hwBroken; // 硬解掛過一次就整場軟解（另有 C:\Temp\ipc-softdec 強制軟解）
    private byte[]? _seqHead;
    private CancellationTokenSource? _pumpCts;
    private WriteableBitmap? _bitmapA, _bitmapB;
    private bool _useA;
    private DateTime _lastShown = DateTime.MinValue;
    private bool _uiReady;
    private string _phoneName = "", _phoneIp = "";
    private WriteableBitmap? _lastShownBitmap;
    private static readonly string PreviewLog =
        Path.Combine(Path.GetTempPath(), "ipc-preview.log");

    private static void PLog(string s)
    {
        try { File.AppendAllText(PreviewLog, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    // ---------- 啟動：虛擬相機＋永遠搜尋 ----------

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "虛擬相機：啟動中…";
        try
        {
            CmbRes.SelectedIndex = 0;
            CmbFps.SelectedIndex = 0;
            ChkSoftDec.IsChecked = File.Exists(@"C:\Temp\ipc-softdec");
            ShowTab("Video");
            HighlightNav("Home");
            _uiReady = true;
        }
        catch { }
        var (ok, msg) = await Task.Run(StartCamera);
        TxtStatus.Text = msg;
        StartBeaconAlways();
    }

    private void StartBeaconAlways()
    {
        try
        {
            _beacon ??= new BeaconListener();
            _beacon.DevicesChanged += devs => Dispatcher.Invoke(() => RefreshPhones(devs));
            _beacon.Start();
            TxtNet.Text = "搜尋中…（手機開 App 按開始廣播）";
        }
        catch (Exception ex)
        {
            TxtNet.Text = "搜尋啟動失敗：" + ex.Message;
        }
    }

    private void RefreshPhones(IReadOnlyList<PhoneDevice> devs)
    {
        object? sel = LstPhones.SelectedItem;
        LstPhones.Items.Clear();
        foreach (var d in devs) LstPhones.Items.Add(d);
        LstPhones.DisplayMemberPath = "Display";
        if (sel is PhoneDevice s)
        {
            foreach (var item in LstPhones.Items)
                if (item is PhoneDevice d && d.Ip == s.Ip) { LstPhones.SelectedItem = item; break; }
        }
        if (LstPhones.SelectedItem == null && LstPhones.Items.Count > 0) LstPhones.SelectedIndex = 0;
        if (_connected) return; // 已連線只刷新清單，不洗掉接收狀態
        TxtNet.Text = $"搜尋中…找到 {devs.Count} 台";
    }

    // ---------- 連線 ----------

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (LstPhones.SelectedItem is not PhoneDevice d)
        {
            TxtNet.Text = "先選一台手機（或下面手動輸 IP）";
            return;
        }
        await ConnectPhone(d.Ip, d.Port);
    }

    private async void BtnManual_Click(object sender, RoutedEventArgs e)
    {
        string ip = TxtIp.Text.Trim();
        if (string.IsNullOrEmpty(ip)) { TxtNet.Text = "先輸手機 IP"; return; }
        await ConnectPhone(ip, CamProto.TcpPort);
    }

    private async Task ConnectPhone(string ip, int port)
    {
        try { _client?.Dispose(); } catch { }
        try
        {
            if (LstPhones.SelectedItem is PhoneDevice sd) { _phoneName = sd.Name; _phoneIp = sd.Ip; }
            else { _phoneName = "手機"; _phoneIp = ip; }
        }
        catch { _phoneName = "手機"; _phoneIp = ip; }
        _client = new CamClient();
        _client.Connected += ep => Dispatcher.Invoke(() =>
        {
            TxtNet.Text = $"接收：已連線（{ep}）";
            TxtHeadConn.Text = $"   ● 已連接手機  {_phoneName}（{_phoneIp}）";
            TxtHeadConn.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#3DFF88"));
            TxtPhone.Text = $"{_phoneName}（{_phoneIp}）";
        });
        _client.Disconnected += ep => Dispatcher.Invoke(() => TxtNet.Text = "接收：已斷線");
        _client.StatsUpdated += s => Dispatcher.Invoke(() =>
        {
            TxtNet.Text = $"接收：{s.Remote}｜{s.Fps:F1}fps｜{s.Mbps:F2}Mbps";
            if (Environment.TickCount64 - _lastNetLogMs > 5000)
            {
                _lastNetLogMs = Environment.TickCount64;
                PLog($"net {s.Fps:F1}fps {s.Mbps:F2}Mbps 累計幀={s.VideoFrames} 丟棄={_drops}");
            }
        });
        _client.ControlReceived += OnNetControl;
        _client.VideoReceived += OnNetVideo;
        TxtNet.Text = $"連線中…{ip}:{port}";
        BtnConnect.IsEnabled = false;
        var (ok, msg) = await _client.ConnectAsync(ip, port);
        TxtNet.Text = (ok ? "接收：" : "") + msg;
        BtnConnect.IsEnabled = true;
        BtnDisconnect.IsEnabled = ok;
        if (ok)
        {
            _connected = true;
            _connMs = Environment.TickCount64;
            try
            {
                _udp?.Dispose();
                _useUdp = false;
                _udp = new UdpReceiver();
                _udp.FrameReady += OnUdpVideo;
                _udp.NackNeeded += seqs =>
                {
                    try { _ = _client?.SendControlAsync("nack", new { seqs }); } catch { }
                };
                _udp.Start();
                _ = _client.SendControlAsync("udp_ready", new { port = UdpReceiver.DefaultPort, host = GetLanIp() });
            }
            catch (Exception ex) { PLog($"UDP 通道啟動失敗（續用 TCP）：{ex.Message}"); }
            _pumpCts = new CancellationTokenSource();
            _ = Task.Run(() => PreviewPump(_pumpCts.Token));
            try { SendSetVideo(); } catch { } // 推送 PC 下拉畫質（斷線期間改的才會生效）
        }
    }

    private static string GetLanIp()
    {
        try
        {
            string? pick = null, first = null;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("127.")) continue;
                    first ??= ip;
                    if (ip.StartsWith("192.168.")) return ip;
                    if (pick == null && (ip.StartsWith("10.") || ip.StartsWith("172."))) pick = ip;
                }
            }
            return pick ?? first ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _ff.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _connected = false;
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _useUdp = false;
        _ffKey = "";
        _previewLabel = "";
        _firstLoggedFor = "";
        _shown = 0; _netIn = 0; _drops = 0; _dropLoggedFor = "";
        _saving = false;
        Dispatcher.Invoke(() => TxtLive.Visibility = Visibility.Collapsed);
        CloseDump();
        TxtNet.Text = "接收：未連線";
        TxtHeadConn.Text = "   ○ 未連接手機";
        TxtHeadConn.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#9AA3C0"));
        TxtPhone.Text = "未連線";
        TxtDecMode.Text = "解碼：-";
        BtnDisconnect.IsEnabled = false;
        try
        {
            ImgPreview.Source = null;
            _lastShownBitmap = null;
            TxtPreview.Text = "等串流…";
            TxtStats.Text = "幀：-";
            BtnSave.Content = "存檔：關";
        }
        catch { }
    }

    // ---------- 存檔 ----------

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _saving = !_saving;
        if (_saving)
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_captures");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"recv-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
                _dump = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                BtnSave.Content = "存檔：開";
                TxtNet.Text += $"（存到 {Path.GetFileName(path)}）";
            }
            catch (Exception ex)
            {
                _saving = false;
                TxtNet.Text = "存檔打不開：" + ex.Message;
            }
        }
        else
        {
            CloseDump();
            BtnSave.Content = "存檔：關";
        }
    }

    private void CloseDump()
    {
        try { lock (_lock) { _dump?.Dispose(); _dump = null; } } catch { }
    }

    private async void BtnRestartEnc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var c = _client;
            if (c == null) { TxtNet.Text = "沒連線，重啟無效"; return; }
            int w, h, fps;
            lock (_lock) { w = _vidW; h = _vidH; fps = _vidFps; }
            if (w <= 0 || h <= 0) { w = 960; h = 720; fps = 30; }
            await c.SendControlAsync("set_video", new { width = w, height = h, fps, codec = "h264" });
            PLog($"已送 set_video（重啟編碼）：{w}x{h}@{fps}");
            TxtNet.Text = "已要求手機重啟編碼…";
        }
        catch (Exception ex) { TxtNet.Text = "重啟要求失敗：" + ex.Message; }
    }

    // ---------- 控制訊息 ----------

    private void OnNetControl(string kind, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (kind == "video_params")
            {
                lock (_lock)
                {
                    if (root.TryGetProperty("width", out var w)) _vidW = w.GetInt32();
                    if (root.TryGetProperty("height", out var h)) _vidH = h.GetInt32();
                    if (root.TryGetProperty("fps", out var f)) _vidFps = f.GetInt32();
                    if (root.TryGetProperty("rot", out var r)) _vidRot = RotNorm(r.GetInt32());
                    if (root.TryGetProperty("flip", out var fl)) _vidFlip = fl.GetInt32() != 0;
                    PLog($"video_params {_vidW}x{_vidH}@{_vidFps} rot={_vidRot} flip={(_vidFlip ? 1 : 0)}");
                }
                TryConfigureDecoder();
            }
            else if (kind == "orientation")
            {
                bool changed = false;
                lock (_lock)
                {
                    if (root.TryGetProperty("rot", out var r))
                    {
                        int nr = RotNorm(r.GetInt32());
                        if (nr != _vidRot) { _vidRot = nr; changed = true; }
                    }
                    if (root.TryGetProperty("flip", out var fl))
                    {
                        bool nf = fl.GetInt32() != 0;
                        if (nf != _vidFlip) { _vidFlip = nf; changed = true; }
                    }
                    if (changed) PLog($"📱方向 rot={_vidRot} flip={(_vidFlip ? 1 : 0)}");
                }
                if (changed) TryConfigureDecoder();
            }
            else if (kind == "codec_config")
            {
                byte[] sps = Convert.FromBase64String(root.GetProperty("sps").GetString()!);
                byte[] pps = Convert.FromBase64String(root.GetProperty("pps").GetString()!);
                byte[] annexb = IsAnnexB(sps) ? sps : SpsPpsToAnnexB(sps, pps);
                lock (_lock) { _seqHead = annexb; if (_saving) _dump?.Write(annexb); }
                TryConfigureDecoder();
            }
            else if (kind == "video_rejected")
            {
                string reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                Dispatcher.Invoke(() => TxtNet.Text = "手機拒收：" + reason);
                PLog("手機 video_rejected：" + reason);
            }
        }
        catch { }
    }

    private static bool IsAnnexB(byte[] b) =>
        b.Length >= 4 && ((b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 1) ||
                          (b[0] == 0 && b[1] == 0 && b[2] == 1));

    private static byte[] SpsPpsToAnnexB(byte[] sps, byte[] pps)
    {
        using var ms = new MemoryStream();
        ms.Write([0, 0, 0, 1]); ms.Write(sps);
        ms.Write([0, 0, 0, 1]); ms.Write(pps);
        return ms.ToArray();
    }

    private static int RotNorm(int r)
    {
        int n = ((r % 360) + 360) % 360;
        return (n == 90 || n == 180 || n == 270) ? n : 0;
    }

    // ---------- 解碼器管理 ----------

    private void TryConfigureDecoder(bool force = false)
    {
        string? uiStartText = null, uiFailText = null, uiCamMsg = null;
        lock (_lock)
        {
            if (_vidW <= 0 || _vidH <= 0) return;
            int effRot = _vidRot;
            bool wantHw = !_hwBroken && !File.Exists(@"C:\Temp\ipc-softdec");
            string basePart = $"h264:{_vidW}x{_vidH}:{_vidFps}:{(wantHw ? "hw" : "sw")}";
            string key = $"{basePart}:r{effRot}:f{(_vidFlip ? 1 : 0)}";
            if (key == _ffKey && _ff.Running) return;
            bool paramOnly = basePart == _ffBase && key != _ffKey;
            if (!force && !paramOnly && _ff.Running && Environment.TickCount64 - _lastFfStartMs < 2000)
            {
                PLog($"重啟略過（防暴衝）：{key} 沿用 {_ffKey}");
                return;
            }
            if (paramOnly) PLog($"參數跟隨：{key}");
            _ffKey = key;
            _lastFfStartMs = Environment.TickCount64;
            try { _ff.Stop(); } catch { }
            string? ff = FfmpegDecoder.FindFfmpeg();
            bool started = ff != null && _ff.Start(_vidW, _vidH, _vidFps, ff, effRot, _vidFlip, wantHw);
            if (started)
            {
                _ffBase = basePart;
                PLog($"解碼器啟動：{key}");
                try { if (_seqHead != null) _ff.Feed(_seqHead); } catch { }
                string facing = _vidFlip ? "前鏡" : "後鏡";
                string portrait = (effRot % 180 != 0) ? "（直向）" : "";
                _previewLabel = $"{_ff.OutW}x{_ff.OutH} {facing} rot={effRot}{portrait} 預覽中・{_ff.HwMode}";
                uiStartText = $"{_ff.OutW}x{_ff.OutH} 預覽解碼器啟動…{facing} rot={effRot}{portrait}・{_ff.HwMode}";
                _ = _client?.SendControlAsync("idr_request", new { });
                PLog("idr_request 已發");
                int ow = _ff.OutW, oh = _ff.OutH;
                if ((ow != _camW || oh != _camH) && IsCamTier(ow, oh))
                {
                    var (rok, rmsg) = RestartCamera(ow, oh);
                    uiCamMsg = (rok ? "✅ " : "❌ ") + rmsg + "（OBS 請重加來源）";
                }
            }
            else if (ff == null)
                uiFailText = "找不到 ffmpeg（C:\\Temp\\ffmpeg）";
        }
        if (uiStartText != null) Dispatcher.Invoke(() => TxtPreview.Text = uiStartText);
        else if (uiFailText != null) Dispatcher.Invoke(() => TxtPreview.Text = uiFailText);
        if (uiCamMsg != null) Dispatcher.Invoke(() => TxtStatus.Text = uiCamMsg);
    }

    // ---------- 影像路徑 ----------

    private void OnNetVideo(CamProto.Frame frame)
    {
        if (_useUdp) return; // UDP 啟用後 TCP 影像只當備援
        HandleVideoBytes(frame.Payload);
    }

    private void OnUdpVideo(UdpReceiver.VideoFrame f)
    {
        try
        {
            if (!_useUdp)
            {
                _useUdp = true;
                PLog("影像來源切換：UDP（TCP 只留控制）");
                try { _ = _client?.SendControlAsync("udp_ok", new { }); } catch { }
            }
            HandleVideoBytes(f.Avcc);
        }
        catch { }
    }

    private void HandleVideoBytes(byte[] payload)
    {
        byte[]? annexb = null;
        try { annexb = AnnexB.ConvertAvcc(payload); } catch { }
        if (annexb == null)
        {
            _drops++;
            if (_dropLoggedFor != _ffKey)
            {
                _dropLoggedFor = _ffKey;
                int n = Math.Min(16, payload.Length);
                PLog($"首丟幀 len={payload.Length} hex={BitConverter.ToString(payload, 0, n)}");
            }
            return;
        }
        try
        {
            if ((_netIn++ % 150) == 0) PLog($"vin {_netIn} annexb={annexb.Length}B saving={_saving}");
            if (_saving) lock (_lock) { _dump?.Write(annexb); };
            if (_seqHead != null && ContainsIdr(annexb))
                _ff.Feed(_seqHead);
            _ff.Feed(annexb);
        }
        catch { }
    }

    private static bool ContainsIdr(byte[] b)
    {
        for (int i = 0; i + 4 < b.Length; i++)
        {
            int sc;
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) sc = 3;
            else if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1) sc = 4;
            else continue;
            if ((b[i + sc] & 0x1F) == 5) return true;
            i += sc;
        }
        return false;
    }

    // ---------- 預覽 pump ----------

    private async Task PreviewPump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_ff.Running && _ffKey != "")
                {
                    string reason = _ff.DeathReason;
                    if (_firstLoggedFor != "dead:" + _ffKey)
                    {
                        _firstLoggedFor = "dead:" + _ffKey;
                        PLog($"解碼器死亡：{reason}");
                    }
                    Dispatcher.Invoke(() =>
                    {
                        string msg = "解碼器停了" + (reason != "" ? "：" + reason : "（重連會自動重啟）");
                        if (TxtPreview.Text != msg) TxtPreview.Text = msg;
                    });
                    if (Environment.TickCount64 - _lastFfStartMs > 3000)
                    {
                        if (!_hwBroken && _ff.HwMode == "硬解" && _ff.FramesRead == 0 && _ff.UpMs < 8000)
                        {
                            string err = _ff.LastErrLines(30).ToLowerInvariant();
                            string[] kws = { "d3d11", "d3d12", "cuda", "hwaccel", "hwdownload", "hardware", "dxva", "qsv", "amf", "cuvid", "nvidia", "gpu" };
                            if (kws.Any(k => err.Contains(k)))
                            {
                                _hwBroken = true;
                                PLog($"硬解不可用，退回軟解：{_ff.DeathReason}");
                            }
                        }
                        PLog("死亡自救：重啟解碼器");
                        TryConfigureDecoder();
                    }
                    else await Task.Delay(500, ct);
                    continue;
                }
                byte[]? nv12 = _ff.TakeLatest();
                if (nv12 == null)
                {
                    if (Environment.TickCount64 - _lastShownLogMs > 5000)
                    {
                        _lastShownLogMs = Environment.TickCount64;
                        PLog($"pump 空轉（顯示 {_shown} 幀 解碼讀幀={_ff.FramesRead} ffCpu={_ff.CpuTime.TotalSeconds:F1}s 餵丟={_ff.FeedDrops} 寫入={_ff.LastFeedLog} 管線={_ff.PipeState()} ferr5=[{_ff.LastErrLines(5)}]）");
                    }
                    await Task.Delay(15, ct);
                    continue;
                }
                if (_firstLoggedFor != _ffKey)
                {
                    _firstLoggedFor = _ffKey;
                    PLog($"首幀已解：{_ff.OutW}x{_ff.OutH} NV12（{nv12.Length}B）");
                    Dispatcher.Invoke(() =>
                    {
                        TxtLive.Visibility = Visibility.Visible;
                        TxtDecMode.Text = $"解碼：{_ff.HwMode}";
                    });
                }
                if ((DateTime.UtcNow - _lastShown).TotalMilliseconds < 33) continue;
                _lastShown = DateTime.UtcNow;
                int w = _ff.OutW, h = _ff.OutH;
                if (w <= 0 || h <= 0 || (w & 1) != 0 || (h & 1) != 0) continue;
                int nvSize = w * h * 3 / 2;
                if (nv12.Length < nvSize) continue;
                // 虛擬攝像頭吃滿幀 NV12 直通，預覽只顯示一半幀的半解析小圖
                try { _frameWriter.Publish(w, h, nv12, SharedFrame.FmtNv12); } catch { }
                if (_shown % 2 == 1) { _shown++; continue; }
                _shown++;
                int pw = w / 2, ph = h / 2;
                byte[] small;
                try { small = Nv12Half.ConvertHalf(nv12, w, h); }
                catch { continue; }
                Dispatcher.Invoke(() =>
                {
                    var target = _useA ? _bitmapA : _bitmapB;
                    _useA = !_useA;
                    if (target == null || target.PixelWidth != pw || target.PixelHeight != ph)
                    {
                        target = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32, null);
                        if (_useA) _bitmapB = target; else _bitmapA = target;
                    }
                    target.WritePixels(new Int32Rect(0, 0, pw, ph), small, pw * 4, 0);
                    ImgPreview.Source = target;
                    _lastShownBitmap = target;
                    string label = _previewLabel != "" ? _previewLabel : $"{w}x{h} 預覽中";
                    if (TxtPreview.Text != label) TxtPreview.Text = label;
                });
                if (Environment.TickCount64 - _lastShownLogMs > 5000)
                {
                    _lastShownLogMs = Environment.TickCount64;
                    if (_connected && !_useUdp && _udp != null
                        && Environment.TickCount64 - _lastFfStartMs > 8000
                        && Environment.TickCount64 - _connMs > 3000)
                    {
                        if (_firstLoggedFor != "udplost:" + _ffKey)
                        {
                            _firstLoggedFor = "udplost:" + _ffKey;
                            PLog("UDP 無幀（被擋？），通知手機續用 TCP");
                            try { _ = _client?.SendControlAsync("udp_lost", new { }); } catch { }
                        }
                    }
                    string udpInfo = "";
                    try
                    {
                        if (_udp != null)
                            udpInfo = $" udp收{_udp.Received}/成{_udp.Complete}/丟{_udp.Lost} 抖{_udp.JitterMs:F0}ms";
                    }
                    catch { }
                    PLog($"顯示 {_shown} 幀 解碼讀幀={_ff.FramesRead} ffCpu={_ff.CpuTime.TotalSeconds:F1}s 餵丟={_ff.FeedDrops}{udpInfo}");
                    string st = $"幀 顯示{_shown}/解碼{_ff.FramesRead} 餵丟{_ff.FeedDrops} CPU{_ff.CpuTime.TotalSeconds:F1}s";
                    Dispatcher.Invoke(() => { if (TxtStats.Text != st) TxtStats.Text = st; });
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ---------- 儀表板：導航＋分頁 ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        HighlightNav(tag);
        if (tag == "Home") { PageHome.Visibility = Visibility.Visible; PageSub.Visibility = Visibility.Collapsed; return; }
        PageHome.Visibility = Visibility.Visible;
        PageSub.Visibility = Visibility.Collapsed;
        if (tag == "Devices") ShowSub("Devices");
        else if (tag == "Settings") ShowSub("Settings");
        else if (tag == "About") ShowSub("About");
    }

    private void HighlightNav(string tag)
    {
        foreach (var b in new[] { NavHome, NavDevices, NavSettings, NavAbout })
        {
            bool on = (b.Tag as string) == tag;
            b.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(on ? "#FF5FD2" : "#9AA3C0"));
            b.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private void ShowSub(string which)
    {
        PageHome.Visibility = Visibility.Collapsed;
        PageSub.Visibility = Visibility.Visible;
        PageDevices.Visibility = which == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = which == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = which == "About" ? Visibility.Visible : Visibility.Collapsed;
        TxtPageTitle.Text = which == "Devices" ? "裝置管理" : which == "Settings" ? "設定" : "關於";
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag) ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        PanelVideo.Visibility = tag == "Video" ? Visibility.Visible : Visibility.Collapsed;
        PanelAdv.Visibility = tag == "Adv" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, on) in new[] { (TabVideo, tag == "Video"), (TabAdv, tag == "Adv") })
        {
            btn.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(on ? "#FF5FD2" : "#9AA3C0"));
            btn.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    // ---------- 畫質下拉 ----------

    private void CmbResFps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _client == null) return;
        SendSetVideo();
    }

    private void SendSetVideo()
    {
        try
        {
            var c = _client;
            if (c == null) return;
            int w = 960, h = 720;
            if (CmbRes.SelectedIndex == 1) { w = 1440; h = 1080; }
            int fps = CmbFps.SelectedIndex == 1 ? 60 : 30;
            _ = c.SendControlAsync("set_video", new { width = w, height = h, fps, codec = "h264" });
            PLog($"已送 set_video：{w}x{h}@{fps}（等手機回 video_params 自動切換）");
        }
        catch { }
    }

    // ---------- 進階：IDR／截圖／最大化／平台／開播 ----------

    private void BtnIdr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_client == null) { TxtNet.Text = "沒連線，要 IDR 無效"; return; }
            _ = _client.SendControlAsync("idr_request", new { });
            PLog("手動 IDR 已發");
        }
        catch { }
    }

    private void BtnSnap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wb = _lastShownBitmap;
            if (wb == null) { TxtPreview.Text = "還沒畫面，截不到"; return; }
            string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_captures");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"snap-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            enc.Save(fs);
            TxtPreview.Text = $"已截圖：{Path.GetFileName(path)}";
        }
        catch (Exception ex) { TxtPreview.Text = "截圖失敗：" + ex.Message; }
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void BtnObs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var obs = Process.GetProcessesByName("obs64").FirstOrDefault();
            if (obs != null)
            {
                ShowWindow(obs.MainWindowHandle, 9);
                SetForegroundWindow(obs.MainWindowHandle);
                TxtPreview.Text = "已切到 OBS（把 iPhoneCam 加為視訊來源即可播出）";
            }
            else
            {
                string path = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path)
                    {
                        WorkingDirectory = @"C:\Program Files\obs-studio\bin\64bit",
                        UseShellExecute = true,
                    });
                    TxtPreview.Text = "OBS 啟動中…";
                }
                else TxtPreview.Text = "找不到 OBS（沒裝？）";
            }
        }
        catch (Exception ex) { TxtPreview.Text = "OBS 開啟失敗：" + ex.Message; }
    }

    private void Platform_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button b && b.Tag is string url)
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LstPhones.SelectedItem == null && LstPhones.Items.Count > 0) LstPhones.SelectedIndex = 0;
            BtnConnect_Click(sender, e);
        }
        catch { }
    }

    private void BtnStopLive_Click(object sender, RoutedEventArgs e)
    {
        BtnDisconnect_Click(sender, e);
    }

    private void ChkSoftDec_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        try
        {
            string flag = @"C:\Temp\ipc-softdec";
            if (ChkSoftDec.IsChecked == true) File.WriteAllText(flag, "soft");
            else File.Delete(flag);
            TxtDecMode.Text = $"解碼：{(ChkSoftDec.IsChecked == true ? "軟解（重連生效）" : "硬解優先")}";
        }
        catch { }
    }

    private async void BtnDiag_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"相機狀態：{TxtStatus.Text}");
        sb.AppendLine($"系統版本：{Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "?")} "
            + $"(DisplayVersion {Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "?")})");
        sb.AppendLine("裝置清單：");
        try
        {
            var devs = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (var d in devs) sb.AppendLine($"  [{(d.IsEnabled ? "啟用" : "停用")}] {d.Name}");
        }
        catch (Exception ex) { sb.AppendLine("  列舉失敗：" + ex.Message); }
        Clipboard.SetText(sb.ToString());
        TxtStatus.Text = "診斷已複製到剪貼簿";
    }

    // ---------- 視窗 chrome ----------

    private void Border_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 虛擬相機 ----------

    private (bool ok, string msg) StartCamera()
    {
        try
        {
            lock (_lock)
            {
                if (!_mfStarted) { MFFunctions.MFStartup(); _mfStarted = true; }
                if (_camera == null)
                {
                    var hr = Functions.MFCreateVirtualCamera(
                        __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
                        __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
                        __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
                        FriendlyName, SourceId, null, 0, out var camera);
                    if (hr.IsError) return (false, $"建立失敗：{hr.Value}（Source DLL 有註冊嗎？）");
                    _camera = new ComObject<IMFVirtualCamera>(camera);
                }
                var shr = _camera.Object.Start(null);
                if (shr.IsError) return (false, $"啟動失敗：{shr.Value}（管理員註冊過了嗎？）");
                return (true, $"虛擬相機：{_camW}x{_camH} 已啟動（{FriendlyName}）");
            }
        }
        catch (Exception ex)
        {
            return (false, $"啟動發生問題：{ex.Message}");
        }
    }

    private (bool ok, string msg) RestartCamera(int w, int h)
    {
        try
        {
            lock (_lock)
            {
                if (w == _camW && h == _camH) return (true, "畫布沒變，不用重建");
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\iPhoneCam");
                    k?.SetValue("CamW", w, RegistryValueKind.DWord);
                    k?.SetValue("CamH", h, RegistryValueKind.DWord);
                }
                catch (Exception ex) { return (false, $"寫登錄失敗：{ex.Message}"); }
                try { _camera?.Object.Remove(); } catch { }
                try { _camera?.Dispose(); } catch { }
                _camera = null;
                _camW = w; _camH = h;
            }
            var (ok, msg) = StartCamera();
            if (ok) PLog($"虛擬攝像頭換畫布：{w}x{h}（OBS 請重加來源或開關眼珠）");
            return (ok, msg);
        }
        catch (Exception ex)
        {
            return (false, $"重建發生問題：{ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _frameWriter.Dispose(); } catch { }
        try { _ff.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _beacon?.Dispose(); } catch { }
        CloseDump();
        try
        {
            lock (_lock)
            {
                _camera?.Object.Remove();
                _camera?.Dispose();
                _camera = null;
                if (_mfStarted) MFFunctions.MFShutdown();
            }
        }
        catch { }
        base.OnClosed(e);
    }
}
