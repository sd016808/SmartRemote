using SmartRemote.App;
using SmartRemote.Core;
using SmartRemote.Service;
using SmartRemote.Tray;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartRemote
{
    static class Program
    {
        const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint context);

        static int Main(string[] args)
        {
            // 系統服務模式的另外兩個角色 (同一個 exe)：服務本體 (服務管理員啟動) 與服務開的遠端程式
            if (args.Contains("--service")) return ServiceHost.Run();
            if (args.Contains("--agent"))
            {
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                return Agent.Run();
            }

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

            // 已經安裝成系統服務：系統匣程式一律用 Program Files 那一份 (設定檔、日誌都在那裡)，這一份就交給它
            bool service = ServiceSetup.Installed;
            if (service && !ServiceSetup.IsInstalledCopy(Environment.ProcessPath) && File.Exists(ServiceSetup.InstalledExe))
            {
                // 開的是比較新的版本 (例如下載了新版的 zip 直接執行)：問要不要把系統服務更新成這一版
                Version installed = Version.TryParse(FileVersionInfo.GetVersionInfo(ServiceSetup.InstalledExe).FileVersion, out var v) ? v : null;
                if (installed != null && AutoUpdater.CurrentVersion > installed &&
                    Win32.Message($"系統服務目前是 v{installed}，要更新成這一版 v{AutoUpdater.CurrentVersion} 嗎？\n\n更新時遠端連線會中斷一下；{ServiceSetup.InstallDir} 裡的設定檔不會變。",
                                  "SmartRemote 系統服務", Win32.MB_YESNO | Win32.MB_ICONQUESTION) == Win32.IDYES)
                {
                    try { ServiceSetup.Install(AppContext.BaseDirectory, copySettings: false); }
                    catch (Exception ex)
                    {
                        Logger.Warn($"更新系統服務失敗: {ex.Message}");
                        Win32.Message($"更新系統服務失敗：\n\n{ex.Message}", "SmartRemote 系統服務", Win32.MB_ICONERROR);
                    }
                }
                Logger.Info($"已經安裝成系統服務，改開 {ServiceSetup.InstalledExe}");
                var info = new ProcessStartInfo(ServiceSetup.InstalledExe) { UseShellExecute = false, WorkingDirectory = ServiceSetup.InstallDir };
                info.ArgumentList.Add(AutoUpdater.AfterUpdateArg);
                Process.Start(info);
                return 0;
            }

            AutoStart.SyncInBackground(settings.AutoStart);   // 排程跟設定一致 (exe 搬過資料夾時路徑會更新)
            ITrayEngine engine = service ? new ServiceEngine() : new LocalEngine(settings);
            new TrayIcon(settings, engine).Run();
            return 0;
        }
    }
}
