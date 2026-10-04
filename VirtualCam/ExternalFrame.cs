using System;
using PcNet;

namespace VCamNetSampleSource
{
    /// <summary>
    /// P2d：從共享記憶體拿手機最新幀，letterbox 進相機當前畫布（動態尺寸，原生 4:3）。
    /// fmt 跟著寫端走（BGRA 舊測試／NV12 直通），呼叫端照 fmt 餵對應分支。
    /// 黑邊只在尺寸變化時填一次（之後只蓋內容列）。所有方法 thread-safe。
    /// </summary>
    internal static class ExternalFrame
    {
        private static readonly SharedFrameReader _reader = new();
        private static readonly object _lock = new();
        private static byte[]? _canvas;
        private static string _canvasKey = "";
        private static readonly string LogPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ipcdll.log");
        private static long _lastLogMs;
        private static bool _loggedStale;

        private static void DLog(string s)
        {
            try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); }
            catch { }
        }

        public static byte[]? TryGetLetterboxed(int cw, int ch, out int fmt)
        {
            fmt = SharedFrame.FmtBgra;
            try
            {
                if (!_reader.TryGetLatest(out var sw, out var sh, out var src, out var fresh))
                {
                    if (!_loggedStale)
                    {
                        _loggedStale = true;
                        DLog("讀不到（" + SharedFrameReader.LastReaderError + "）");
                    }
                    return null;
                }
                if (!fresh || src == null || sw <= 0 || sh <= 0)
                {
                    // 斷訊就凍結上一幀（真攝影機的行為），從沒收過才回 null 畫彩條
                    lock (_lock) { return _canvas; }
                }
                _loggedStale = false;
                long now = System.Environment.TickCount64;
                if (now - _lastLogMs > 5000)
                {
                    _lastLogMs = now;
                    DLog($"新幀 {sw}x{sh} fmt={_reader.Format}（letterbox進{cw}x{ch}）");
                }
                fmt = _reader.Format;
                lock (_lock)
                {
                    if (fmt == SharedFrame.FmtNv12)
                        return LetterboxNv12(cw, ch, sw, sh, src);
                    int n = cw * ch * 4;
                    if (_canvas == null || _canvas.Length != n) _canvas = new byte[n];
                    Array.Clear(_canvas, 0, n);
                    double s = Math.Min((double)cw / sw, (double)ch / sh);
                    int dw = Math.Max(1, (int)(sw * s)), dh = Math.Max(1, (int)(sh * s));
                    int dx = (cw - dw) / 2, dy = (ch - dh) / 2;
                    int srcStride = sw * 4, dstStride = cw * 4;
                    for (int y = 0; y < dh; y++)
                    {
                        int sy = Math.Min(sh - 1, (int)((long)y * sh / dh));
                        Buffer.BlockCopy(src, sy * srcStride, _canvas, (dy + y) * dstStride + dx * 4, dw * 4);
                    }
                    return _canvas;
                }
            }
            catch { return null; }
        }

        /// <summary>NV12 letterbox：Y 16＋UV 128 填底（只在尺寸變化時填），內容列逐列拷貝。</summary>
        private static byte[] LetterboxNv12(int cw, int ch, int sw, int sh, byte[] src)
        {
            int n = cw * ch * 3 / 2;
            string key = $"{cw}x{ch}:{sw}x{sh}";
            if (_canvas == null || _canvas.Length != n || _canvasKey != key)
            {
                _canvas = new byte[n];
                _canvasKey = key;
                int ySize = cw * ch;
                for (int i = 0; i < ySize; i++) _canvas[i] = 16;
                for (int i = ySize; i < n; i++) _canvas[i] = 128;
            }
            double s = Math.Min((double)cw / sw, (double)ch / sh);
            int dw = Math.Max(2, ((int)(sw * s)) & ~1), dh = Math.Max(2, ((int)(sh * s)) & ~1);
            int dx = ((cw - dw) / 2) & ~1, dy = ((ch - dh) / 2) & ~1;
            int ySize2 = cw * ch;
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(sh - 1, (int)((long)y * sh / dh));
                Buffer.BlockCopy(src, sy * sw, _canvas!, (dy + y) * cw + dx, dw);
            }
            int uvH = dh / 2, uvW = dw;
            int srcUv = sw * sh, dstUv = ySize2;
            for (int y = 0; y < uvH; y++)
            {
                int sy = Math.Min(sh / 2 - 1, (int)((long)y * (sh / 2) / uvH));
                Buffer.BlockCopy(src, srcUv + sy * sw, _canvas!, dstUv + (dy / 2 + y) * cw + dx, uvW);
            }
            return _canvas;
        }
    }
}
