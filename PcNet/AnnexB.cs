using System.Buffers.Binary;

namespace PcNet;

/// <summary>AVCC（長度前綴）→ Annex B（起始碼）。手機實機吐的已是 Annex-B→直通；
/// 純 AVCC 才轉換；格式不合回 null（呼叫端跳過該訊框，不炸）。</summary>
public static class AnnexB
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    public static byte[]? ConvertAvcc(byte[] avcc)
    {
        try
        {
            if (avcc.Length >= 4 &&
                ((avcc[0] == 0 && avcc[1] == 0 && avcc[2] == 0 && avcc[3] == 1) ||
                 (avcc[0] == 0 && avcc[1] == 0 && avcc[2] == 1)))
                return avcc;
            using var ms = new MemoryStream(avcc.Length + 64);
            int pos = 0;
            while (pos + 4 <= avcc.Length)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(avcc.AsSpan(pos, 4));
                pos += 4;
                if (len <= 0 || pos + len > avcc.Length) return null;
                ms.Write(StartCode);
                ms.Write(avcc.AsSpan(pos, len));
                pos += len;
            }
            if (pos != avcc.Length) return null;
            return ms.ToArray();
        }
        catch { return null; }
    }
}
