namespace VCamNetSampleSource
{
    public static class Shared
    {
        public const string CLSID_VCamNet = "8136b935-e87f-4c31-9456-31eedfd84776"; // P2d後：舊 CLSID 疑似被 OBS/FrameServer 快取，換新斷乾淨
    }

    /// <summary>
    /// 虛擬攝像頭畫布尺寸：跟著串流走（App 寫 HKCU，DLL 啟動時讀）。
    /// 只吃檔位表內尺寸（v2 兩檔 4:3＋直向，其它一律回 720P）。
    /// </summary>
    public static class CameraConfig
    {
        public const string RegPath = @"SOFTWARE\iPhoneCam";
        public static readonly (int w, int h)[] Tiers =
        {
            (960, 720), (1440, 1080),
            (720, 960), (1080, 1440),
        };

        public static (int w, int h) ReadSize()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);
                if (k != null)
                {
                    object? w = k.GetValue("CamW"), h = k.GetValue("CamH");
                    if (w is int wi && h is int hi)
                    {
                        foreach (var t in Tiers)
                            if (t.w == wi && t.h == hi) return t;
                    }
                }
            }
            catch { }
            return (960, 720);
        }

        public static bool IsTier(int w, int h)
        {
            foreach (var t in Tiers)
                if (t.w == w && t.h == h) return true;
            return false;
        }
    }
}
