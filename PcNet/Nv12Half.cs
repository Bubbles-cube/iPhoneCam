namespace PcNet;

/// <summary>
/// NV12 → 半解析 BGRA（預覽小圖專用）。最近點 2x 取樣＋BT.601 整數轉換。
/// 虛擬攝像頭走全尺寸 NV12 直通，不經這裡。</summary>
public static class Nv12Half
{
    public static byte[] ConvertHalf(byte[] nv12, int w, int h)
    {
        int pw = w / 2, ph = h / 2;
        byte[] out_ = new byte[pw * ph * 4];
        int ySize = w * h;
        for (int y = 0; y < ph; y++)
        {
            int sy = y * 2;
            int uvRow = ySize + (sy >> 1) * w;
            for (int x = 0; x < pw; x++)
            {
                int sx = x * 2;
                int c = nv12[sy * w + sx] - 16;
                int d = nv12[uvRow + (sx & ~1)] - 128;
                int e = nv12[uvRow + (sx & ~1) + 1] - 128;
                int r = (298 * c + 409 * e + 128) >> 8;
                int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int b = (298 * c + 516 * d + 128) >> 8;
                int o = (y * pw + x) * 4;
                out_[o] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                out_[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                out_[o + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                out_[o + 3] = 255;
            }
        }
        return out_;
    }
}
