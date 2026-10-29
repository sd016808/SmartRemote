using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>
    /// 遠端畫面用到的 Win32 / COM 呼叫。COM 介面不用 [ComImport] (AOT 不支援內建的 COM 互通)，
    /// 直接照 vtable 的順序取函式指標呼叫：Slot(p, n) = 介面 p 的第 n 個方法 (IUnknown 的 QueryInterface / AddRef / Release 是 0 / 1 / 2)
    /// </summary>
    static unsafe class Native
    {
        public static IntPtr Slot(IntPtr p, int n) => (*(IntPtr**)p)[n];

        public static int QueryInterface(IntPtr p, in Guid iid, out IntPtr result)
        {
            fixed (Guid* g = &iid)
            fixed (IntPtr* r = &result)
                return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(p, 0))(p, g, r);
        }

        public static void Release(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, 2))(p);
            p = IntPtr.Zero;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException($"{what} 失敗 (0x{hr:X8})", hr);
        }

        // ===== DPI：遠端畫面的執行緒一律用「每個螢幕各自的 DPI」，座標都是實際像素 =====
        // 主程式是 SetProcessDPIAware (系統 DPI)，各螢幕縮放不同時，副螢幕的座標會被 Windows 換算過；只改這條執行緒，不影響 bot
        static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        public static void UsePerMonitorDpi()
        {
            try { SetThreadDpiAwarenessContext(PerMonitorAwareV2); } catch (EntryPointNotFoundException) { }
        }

        /// <summary>暫時切成每個螢幕各自的 DPI (執行緒集區的執行緒用完要還原)</summary>
        public static DpiScope PerMonitorDpi()
        {
            try { return new DpiScope(SetThreadDpiAwarenessContext(PerMonitorAwareV2)); }
            catch (EntryPointNotFoundException) { return new DpiScope(IntPtr.Zero); }
        }

        public readonly struct DpiScope : IDisposable
        {
            readonly IntPtr old;
            public DpiScope(IntPtr old) => this.old = old;
            public void Dispose() { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
        }

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        // ===== 精確等待 =====
        // Thread.Sleep 的精度約 15.6 ms (要等 4 ms 也會等 16 ms)，60 張/秒會被壓成 30 張，所以用高精度的計時器 (Win10 1803 以後才有，沒有就退回 Thread.Sleep)
        [ThreadStatic] static IntPtr timer;

        public static void PreciseSleep(int ms)
        {
            if (ms <= 0) return;
            if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003); // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION、TIMER_ALL_ACCESS
            long due = -ms * 10000L; // 相對時間，100 ns 為單位
            if (timer == IntPtr.Zero || !SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) { Thread.Sleep(ms); return; }
            WaitForSingleObject(timer, 0xFFFFFFFF);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        // ===== 螢幕 =====
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        public sealed record Monitor(IntPtr Handle, OpenCvSharp.Rect Bounds, bool Primary);

        /// <summary>所有螢幕 (桌面座標，實際像素)</summary>
        public static List<Monitor> Monitors()
        {
            var list = new List<Monitor>();
            using (PerMonitorDpi())
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    bool primary = GetMonitorInfo(h, ref info) && (info.dwFlags & 1) != 0;
                    list.Add(new Monitor(h, new OpenCvSharp.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), primary));
                    return true;
                }, IntPtr.Zero);
            }
            if (list.Count == 0) list.Add(new Monitor(IntPtr.Zero, new OpenCvSharp.Rect(0, 0, 1920, 1080), true));
            return list;
        }

        // ===== 游標 (Desktop Duplication 不能用時，GDI 截圖自己畫游標) =====
        [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    }
}
