using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PcNet;

/// <summary>
/// App → 虛擬攝像頭 Source DLL 的跨處理程序影格通道（P2d v2：Global 版）。
/// 背景：不管有沒有 FRAMESERVER_SHARED，Session 虛擬相機一律由系統 FrameServer
/// 代管（SYSTEM／session 0，實測：它鎖住 DLL 檔＋svc log 證實代管），看不到使用者
/// session 的具名物件 → 通道必須放 Global 命名空間＋Everyone 可開。
/// 生命週期反轉：讀端（DLL，通常有權限）Create，寫端（App）Open；沒人看＝不發。
/// MMF "Global\iPhoneCam.Frame.v1"＋mutex；標頭 64B：magic u32, ver u32, w i32,
/// h i32, seq i64, tickMs i64, fmt i32(32)。payload：fmt=0 BGRA(w*h*4)／fmt=1 NV12(w*h*3/2)，
/// 上限 3840x2160xBGRA（NV12 一定裝得下）。讀端永遠 WaitOne(0)（絕不擋 MF 管線），
/// 500ms 沒新幀就算斷訊（回測試圖）。
/// </summary>
public static class SharedFrame
{
    public const string MapName = @"Global\iPhoneCam.Frame.v1";
    public const string MutexName = @"Global\iPhoneCam.Frame.v1.mutex";
    public const uint Magic = 0x43414D46; // "CAMF"
    public const int HeaderSize = 64;
    public const int FmtBgra = 0, FmtNv12 = 1;
    public const int MaxPayload = 3840 * 2160 * 4;
    public static readonly long MapSize = HeaderSize + (long)MaxPayload;
    public const int FreshMs = 500;

    public static int PayloadSize(int w, int h, int fmt) =>
        fmt == FmtNv12 ? w * h * 3 / 2 : w * h * 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out nint sd, out uint len);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMapping(
        nint hFile, ref SECURITY_ATTRIBUTES sa, uint protect,
        uint sizeHi, uint sizeLo, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint h);

    /// <summary>建 Everyone-full 的 Global MMF（給有權限的讀端呼叫）。
    /// 注意：回傳的 handle 必須一直拿著（存在 static），放掉物件就沒了。</summary>
    internal static nint CreateGlobalMap()
    {
        nint sd = 0;
        nint map = nint.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:(A;;GA;;;WD)", 1, out sd, out _))
            {
                LastCreateError = "sddl-fail:" + Marshal.GetLastWin32Error();
                return nint.Zero;
            }
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = false
            };
            const uint PAGE_READWRITE = 4;
            long size = MapSize;
            map = CreateFileMapping(new nint(-1), ref sa, PAGE_READWRITE,
                (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), MapName);
            if (map == nint.Zero)
                LastCreateError = "createfail:" + Marshal.GetLastWin32Error();
            return map; // 呼叫端留著，不要關
        }
        catch (Exception e) { LastCreateError = "ex:" + e.GetType().Name; return nint.Zero; }
        finally
        {
            if (sd != nint.Zero) { try { LocalFree(sd); } catch { } }
        }
    }

    internal static string LastCreateError = "";

    internal static Mutex CreateGlobalMutex()
    {
        var ms = new MutexSecurity();
        ms.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        bool created;
        return System.Threading.MutexAcl.Create(false, MutexName, out created, ms);
    }
}

public sealed class SharedFrameWriter : IDisposable
{
    private MemoryMappedFile? _mmf;
    private Mutex? _mutex;
    private MemoryMappedViewAccessor? _acc;
    private bool _disposed;

    private bool Ensure()
    {
        if (_acc != null) return true;
        try
        {
            // 只開不建：沒消費者（DLL 還沒建 Global）就跳過，不浪費
            _mutex = Mutex.OpenExisting(SharedFrame.MutexName);
            _mmf = MemoryMappedFile.OpenExisting(SharedFrame.MapName);
            _acc = _mmf.CreateViewAccessor();
            return true;
        }
        catch (Exception e) { LastOpenError = e.GetType().Name + ":" + e.Message; return false; }
    }

    public static string LastOpenError = "";

    public bool Publish(int w, int h, byte[] pixels, int fmt = SharedFrame.FmtBgra)
    {
        try
        {
            if (w <= 0 || h <= 0 || w > 3840 || h > 2160) return false;
            if ((w & 1) != 0 || (h & 1) != 0) return false; // NV12 寬高必須偶數，BGRA 順便一起要求
            int n = SharedFrame.PayloadSize(w, h, fmt);
            if (pixels.Length < n) return false;
            if (!Ensure() || _mutex == null) return false;
            bool taken = false;
            try { taken = _mutex!.WaitOne(15); } // 有人拿著就跳過這幀，不等
            catch (AbandonedMutexException) { taken = true; } // 前持有者死了，锁算我们的
            if (!taken) return false;
            try
            {
                _acc!.Write(0, SharedFrame.Magic);
                _acc.Write(4, 1);
                _acc.Write(8, w);
                _acc.Write(12, h);
                _acc.Write(16, DateTime.UtcNow.Ticks);
                _acc.Write(24, Environment.TickCount64);
                _acc.Write(32, fmt);
                _acc.WriteArray(SharedFrame.HeaderSize, pixels, 0, n);
                return true;
            }
            finally { try { _mutex.ReleaseMutex(); } catch { } }
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _acc?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _acc = null; _mmf = null; _mutex = null;
    }
}

public sealed class SharedFrameReader
{
    private MemoryMappedFile? _mmf;
    private Mutex? _mutex;
    private MemoryMappedViewAccessor? _acc;
    private long _lastTicks = -1;
    private long _lastLocalMs;
    private byte[]? _buf;
    private int _w, _h, _fmt;
    /// <summary>目前緩衝的格式（FmtBgra/FmtNv12）。</summary>
    public int Format => _fmt;
    private static nint _mapHandle; // 建完一直拿著，放掉 Global 物件就消失
    public static string LastReaderError = "";

    private bool Ensure()
    {
        if (_acc != null) return true;
        try
        {
            // 讀端負責建（它通常跑在 SYSTEM/FrameServer，有建 Global 的權限）
            if (_mapHandle == nint.Zero)
            {
                _mapHandle = SharedFrame.CreateGlobalMap();
                if (_mapHandle == nint.Zero)
                {
                    LastReaderError = "create:" + SharedFrame.LastCreateError;
                    return false;
                }
            }
            _mmf = MemoryMappedFile.OpenExisting(SharedFrame.MapName);
            try { _mutex = Mutex.OpenExisting(SharedFrame.MutexName); }
            catch
            {
                try { _mutex = SharedFrame.CreateGlobalMutex(); }
                catch (Exception e2)
                {
                    LastReaderError = "mutex:" + e2.GetType().Name + ":" + e2.Message;
                    return false;
                }
            }
            _acc = _mmf.CreateViewAccessor();
            LastReaderError = "";
            return true;
        }
        catch (Exception e)
        {
            LastReaderError = "open:" + e.GetType().Name + ":" + e.Message;
            return false;
        }
    }

    /// <summary>拿最新幀（有就複製到內部緩衝）。fresh＝500ms 內有新 seq。data 格式看 Format。</summary>
    public bool TryGetLatest(out int w, out int h, out byte[]? data, out bool fresh)
    {
        w = _w; h = _h; data = _buf;
        fresh = false;
        try
        {
            if (!Ensure()) return _buf != null;
            bool taken = false;
            try { taken = _mutex!.WaitOne(0); }
            catch (AbandonedMutexException) { taken = true; }
            if (!taken) return _buf != null;
            try
            {
                if (_acc!.ReadUInt32(0) != SharedFrame.Magic) return _buf != null;
                int sw = _acc.ReadInt32(8), sh = _acc.ReadInt32(12);
                long seq = _acc.ReadInt64(16);
                int fmt = _acc.ReadInt32(32);
                if (fmt != SharedFrame.FmtBgra && fmt != SharedFrame.FmtNv12) fmt = SharedFrame.FmtBgra; // 舊寫端沒寫 fmt 欄（全 0）＝BGRA
                if (sw <= 0 || sh <= 0 || sw > 3840 || sh > 2160) return _buf != null;
                if (seq != _lastTicks)
                {
                    int n = SharedFrame.PayloadSize(sw, sh, fmt);
                    if (_buf == null || _buf.Length != n) _buf = new byte[n];
                    int read = _acc.ReadArray(SharedFrame.HeaderSize, _buf, 0, n);
                    if (read != n) return _buf != null;
                    _lastTicks = seq;
                    _w = sw; _h = sh; _fmt = fmt;
                    _lastLocalMs = Environment.TickCount64;
                }
            }
            finally { try { _mutex?.ReleaseMutex(); } catch { } }
        }
        catch { }
        w = _w; h = _h; data = _buf;
        fresh = _buf != null && (Environment.TickCount64 - _lastLocalMs) <= SharedFrame.FreshMs;
        return _buf != null;
    }
}
