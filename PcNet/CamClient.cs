using System.Net.Sockets;
using System.Text;

namespace PcNet;

/// <summary>CAM1 主動連線端（手機監聽 19300，PC 連過去）。
/// 收訊迴圈跑背景 Task：下游 Feed（pipe 寫入）可能擋住，絕不能沾 UI context（v1 卡死教訓）。</summary>
public sealed class CamClient : IDisposable
{
    public event Action<string>? Connected;
    public event Action<string>? Disconnected;
    public event Action<CamProto.Frame>? VideoReceived;
    public event Action<string, string>? ControlReceived;
    public event Action<CamProto.Stats>? StatsUpdated;

    private TcpClient? _tcp;
    private CancellationTokenSource? _cts;
    private long _videoFrames, _videoBytes, _windowFrames, _windowBytes;
    private DateTime _windowStart = DateTime.UtcNow;
    private string _remote = "";
    private bool _disposed;

    public bool IsConnected => _tcp?.Connected == true;

    public async Task<(bool ok, string msg)> ConnectAsync(string ip, int port)
    {
        Disconnect();
        _cts = new CancellationTokenSource();
        try
        {
            _tcp = new TcpClient();
            _tcp.NoDelay = true; // 小 NAL 別被 Nagle 拖
            await _tcp.ConnectAsync(ip, port, _cts.Token).ConfigureAwait(false);
            _remote = $"{ip}:{port}";
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            return (true, $"已連線 {_remote}");
        }
        catch (Exception e)
        {
            Disconnect();
            return (false, "連不上：" + e.Message);
        }
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Close(); } catch { }
        _tcp = null;
    }

    public async Task SendControlAsync(string kind, object message)
    {
        var tcp = _tcp;
        if (tcp == null || !tcp.Connected) return;
        try { await CamProto.SendControlAsync(tcp.GetStream(), kind, message, CancellationToken.None); }
        catch { }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        NetworkStream stream;
        try { stream = _tcp!.GetStream(); }
        catch { return; }
        Connected?.Invoke(_remote);
        try
        {
            await CamProto.SendControlAsync(stream, "hello",
                new { app = "iPhoneCam-PC", version = "v2", device = Environment.MachineName }, ct);
            while (!ct.IsCancellationRequested && _tcp?.Connected == true)
            {
                var frame = await CamProto.ReceiveAsync(stream, ct);
                switch (frame.Type)
                {
                    case CamProto.TypeVideo:
                        _videoFrames++; _videoBytes += frame.Payload.Length;
                        _windowFrames++; _windowBytes += frame.Payload.Length;
                        VideoReceived?.Invoke(frame);
                        break;
                    case CamProto.TypeControl:
                        string json = Encoding.UTF8.GetString(frame.Payload);
                        string kind = "";
                        try { kind = CamProto.ControlKind(json); } catch { }
                        ControlReceived?.Invoke(kind, json);
                        break;
                }
                TickStats();
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        Disconnected?.Invoke(_remote);
    }

    private void TickStats()
    {
        var now = DateTime.UtcNow;
        double sec = (now - _windowStart).TotalSeconds;
        if (sec >= 1.0)
        {
            StatsUpdated?.Invoke(new CamProto.Stats(_remote, _videoFrames,
                _windowFrames / sec, _windowBytes * 8.0 / sec / 1_000_000.0));
            _windowFrames = 0; _windowBytes = 0; _windowStart = now;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        try { _cts?.Dispose(); } catch { }
    }
}
