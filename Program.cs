using SmartRemote.Core;
using SmartRemote.Tray;
using System.Runtime.InteropServices;

namespace SmartRemote
{
    static class Program
    {
        const uint MB_OK = 0x0000, MB_ICONERROR = 0x0010, MB_ICONINFORMATION = 0x0040;
        const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [DllImport("user32.dll", SetLastError = true)] static extern int MessageBoxW(IntPtr owner, IntPtr text, IntPtr caption, uint type);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint context);

        static int Main()
        {
            // 同時只開一個 (兩個同時開會各自等待、房間碼與圖示都亂)
            using var mutex = new Mutex(true, "SmartRemote-SingleInstance", out bool first);
            if (!first)
            {
                Message("SmartRemote 已經在執行中，請看右下角系統匣的圖示。", "SmartRemote", MB_ICONINFORMATION);
                return 0;
            }

            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // 截圖與編碼用到 OpenCV 的原生 dll，要跟 exe 放在一起
            if (!NativeLibrary.TryLoad("OpenCvSharpExtern", typeof(Program).Assembly, null, out _))
            {
                Message($"找不到 OpenCvSharpExtern.dll，請把 SmartRemote.exe 跟資料夾裡的所有 dll 放在一起：{AppContext.BaseDirectory}", "SmartRemote", MB_ICONERROR);
                return 1;
            }

            Settings settings;
            try { settings = Settings.Load(); }
            catch (FormatException ex)
            {
                Message(ex.Message, "SmartRemote 設定錯誤", MB_ICONERROR);
                return 1;
            }

            Logger.Info("========== 啟動 SmartRemote ==========");
            new TrayIcon(settings).Run();
            return 0;
        }

        static void Message(string text, string caption, uint type)
        {
            IntPtr t = Marshal.StringToHGlobalUni(text), c = Marshal.StringToHGlobalUni(caption);
            try { MessageBoxW(IntPtr.Zero, t, c, MB_OK | type); }
            finally { Marshal.FreeHGlobal(t); Marshal.FreeHGlobal(c); }
        }
    }
}
