using SmartRemote.Core;
using SmartRemote.Tray;
using System.Runtime.InteropServices;

namespace SmartRemote
{
    static class Program
    {
        const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint context);

        static int Main(string[] args)
        {
            // 同時只開一個 (兩個同時開會各自等待、房間碼與圖示都亂)。
            // 更新後重新啟動的新程式會等舊程式結束 (最多 30 秒) 再接手
            bool afterUpdate = args.Contains(AutoUpdater.AfterUpdateArg);
            using var mutex = new Mutex(false, "SmartRemote-SingleInstance");
            bool owned;
            try { owned = mutex.WaitOne(afterUpdate ? TimeSpan.FromSeconds(30) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { owned = true; } // 上一個程式異常結束，鎖還在，可以接手
            if (!owned)
            {
                Win32.Message("SmartRemote 已經在執行中，請看右下角系統匣的圖示。", "SmartRemote", Win32.MB_ICONINFORMATION);
                return 0;
            }

            AutoUpdater.CleanupOldFiles();
            Logger.Info($"========== 啟動 SmartRemote v{AutoUpdater.CurrentVersion} ==========");

            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // 截圖與編碼用到 OpenCV 的原生 dll，要跟 exe 放在一起
            if (!NativeLibrary.TryLoad("OpenCvSharpExtern", typeof(Program).Assembly, null, out _))
            {
                Win32.Message($"找不到 OpenCvSharpExtern.dll，請把 SmartRemote.exe 跟資料夾裡的所有 dll 放在一起：{AppContext.BaseDirectory}", "SmartRemote", Win32.MB_ICONERROR);
                return 1;
            }

            Settings settings;
            try { settings = Settings.Load(); }
            catch (FormatException ex)
            {
                Win32.Message(ex.Message, "SmartRemote 設定錯誤", Win32.MB_ICONERROR);
                return 1;
            }

            AutoStart.SyncInBackground(settings.AutoStart);   // 排程跟設定一致 (exe 搬過資料夾時路徑會更新)
            new TrayIcon(settings).Run();
            return 0;
        }
    }
}
