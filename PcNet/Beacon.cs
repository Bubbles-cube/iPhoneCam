using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PcNet;

/// <summary>手機裝置（UDP 信標收集）。Display 直接顯示在清單。</summary>
public sealed record PhoneDevice(string Name, string Ip, int Port, string Codecs, string Max, DateTime LastSeen)
{
    public string Display => $"{Name}（{Ip}）[{Max}]";
}

/// <summary>UDP 信標監聽：手機每 2 秒廣播到 19301，收集去重、15 秒逾時移除。
/// 同 IP 檔位／編碼變了也刷新事件（手機切畫質 PC 跟著更新）。</summary>
public sealed class BeaconListener : IDisposable
{
    public const int BeaconPort = 19301;
    public event Action<IReadOnlyList<PhoneDevice>>? DevicesChanged;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, PhoneDevice> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;

    public void Start()
    {
        if (_udp != null) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(BeaconPort);
        _ = ReceiveLoop(_cts.Token);
        _ = ExpireLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _udp?.Close(); } catch { }
        _udp = null;
    }

    public IReadOnlyList<PhoneDevice> Snapshot()
    {
        lock (_lock) return _devices.Values.OrderBy(d => d.Name).ToList();
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(ct);
                string json = Encoding.UTF8.GetString(r.Buffer);
                PhoneDevice? dev = Parse(json, r.RemoteEndPoint);
                if (dev == null) continue;
                bool changed;
                lock (_lock)
                {
                    changed = !_devices.TryGetValue(dev.Ip, out var old)
                        || old.Max != dev.Max || old.Codecs != dev.Codecs || old.Name != dev.Name;
                    _devices[dev.Ip] = dev;
                }
                if (changed) DevicesChanged?.Invoke(Snapshot());
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task ExpireLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                bool changed = false;
                lock (_lock)
                {
                    var old = _devices.Values.Where(d => (DateTime.UtcNow - d.LastSeen).TotalSeconds > 15).ToList();
                    foreach (var d in old) { _devices.Remove(d.Ip); changed = true; }
                }
                if (changed) DevicesChanged?.Invoke(Snapshot());
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private static PhoneDevice? Parse(string json, EndPoint remote)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("kind", out var k) && k.GetString() != "beacon") return null;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            string ip = root.TryGetProperty("ip", out var i) ? i.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(ip) && remote is IPEndPoint ep) ip = ep.Address.ToString();
            int port = root.TryGetProperty("port", out var p) ? p.GetInt32() : CamProto.TcpPort;
            string codecs = "h264";
            if (root.TryGetProperty("codec", out var c))
                codecs = c.ValueKind == JsonValueKind.Array
                    ? string.Join(",", c.EnumerateArray().Select(e => e.GetString()))
                    : c.GetString() ?? "h264";
            string max = root.TryGetProperty("max", out var m) ? m.GetString() ?? "" : "";
            return new PhoneDevice(name, ip, port, codecs, max, DateTime.UtcNow);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
