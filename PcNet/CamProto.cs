using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PcNet;

/// <summary>CAM1 訊框（TCP）：[MAGIC 4B][型別 1B][時間戳 8B][長度 4B][酬載]，全大端序。
/// 型別 0x01=影像(AVCC)／0x03=控制(JSON 內含 kind)。0x02 保留（純視訊）。</summary>
public static class CamProto
{
    public const int TcpPort = 19300;
    public const int BeaconPort = 19301;
    public const int UdpPort = 19302;
    public const uint Magic = 0x43414D31; // "CAM1"
    public const byte TypeVideo = 0x01;
    public const byte TypeControl = 0x03;
    public const int HeaderSize = 4 + 1 + 8 + 4;
    public const int MaxPayload = 8 * 1024 * 1024;

    public sealed record Frame(byte Type, long TimestampMicros, byte[] Payload);
    public sealed record Stats(string Remote, long VideoFrames, double Fps, double Mbps);

    public static async Task SendAsync(NetworkStream stream, byte type, long timestampMicros,
        byte[] payload, CancellationToken ct)
    {
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
        header[4] = type;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(5, 8), timestampMicros);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(13, 4), payload.Length);
        await stream.WriteAsync(header, ct);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct);
    }

    public static async Task SendControlAsync(NetworkStream stream, string kind, object message, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(message)!.AsObject();
        node["kind"] = kind;
        await SendAsync(stream, TypeControl, NowMicros(), Encoding.UTF8.GetBytes(node.ToJsonString()), ct);
    }

    public static async Task<Frame> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = await ReadExactAsync(stream, HeaderSize, ct);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (magic != Magic) throw new InvalidDataException($"MAGIC 不符：0x{magic:X8}");
        byte type = header[4];
        long ts = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(5, 8));
        int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(13, 4));
        if (len < 0 || len > MaxPayload) throw new InvalidDataException($"長度不合理：{len}");
        byte[] payload = len == 0 ? [] : await ReadExactAsync(stream, len, ct);
        return new Frame(type, ts, payload);
    }

    public static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) throw new EndOfStreamException("對端已斷線");
            got += n;
        }
        return buf;
    }

    public static long NowMicros() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

    public static string ControlKind(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
    }
}
