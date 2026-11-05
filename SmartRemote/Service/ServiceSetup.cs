using SmartRemote.App;
using SmartRemote.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartRemote.Service
{
    /// <summary>
    /// 系統服務的安裝 / 移除 / 重新啟動 (右鍵選單「以系統服務執行」)。要系統管理員權限 (系統匣程式本來就有)。
    /// 安裝時把程式複製到 C:\Program Files\SmartRemote 再從那裡執行：服務是 SYSTEM 權限，
    /// 程式放在一般使用者能改的資料夾 (例如 D:\ 底下) 的話，別人換掉 exe 就能拿到最高權限
    /// </summary>
    static class ServiceSetup
    {
        public const string Name = "SmartRemote";
        const string DisplayName = "SmartRemote 遠端畫面";
        const string Description = "讓 SmartRemote 在 UAC 確認視窗、鎖定畫面、登入畫面也能遠端看與操作；開機就啟動，還沒有人登入也能連。";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SmartRemote");
        public static string InstalledExe => Path.Combine(InstallDir, "SmartRemote.exe");

        public enum State { NotInstalled, Stopped, Starting, Running, Stopping }

        /// <summary>服務目前的狀態 (查不到 = 沒安裝)</summary>
        public static State Query()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return State.NotInstalled;
            try
            {
                IntPtr svc = OpenServiceW(scm, Name, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return State.NotInstalled;
                try
                {
                    if (!QueryServiceStatus(svc, out SERVICE_STATUS st)) return State.Stopped;
                    return st.dwCurrentState switch
                    {
                        SERVICE_RUNNING => State.Running,
                        SERVICE_START_PENDING or SERVICE_CONTINUE_PENDING => State.Starting,
                        SERVICE_STOP_PENDING or SERVICE_PAUSE_PENDING => State.Stopping,
                        _ => State.Stopped,
                    };
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public static bool Installed => Query() != State.NotInstalled;

        /// <summary>這個程式是不是 Program Files 裡安裝的那一份</summary>
        public static bool IsInstalledCopy(string exePath) =>
            string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 安裝 (已經裝過就更新)：程式複製到 Program Files、設定檔只給系統管理員讀寫、建立並啟動服務。
        /// copySettings = false：更新程式而已，Program Files 裡原本的設定檔不動。失敗丟例外 (訊息給使用者看)
        /// </summary>
        public static void Install(string sourceDir, bool copySettings = true)
        {
            Logger.Info($"安裝系統服務：從 {sourceDir} 複製到 {InstallDir}");
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw Error("開啟服務管理員失敗");
            try
            {
                // 已經裝過：先停掉 (舊的遠端程式會結束)，才能換檔
                IntPtr old = OpenServiceW(scm, Name, SERVICE_ALL_ACCESS);
                if (old != IntPtr.Zero)
                {
                    try { StopAndWait(old, 15000); }
                    finally { CloseServiceHandle(old); }
                }

                CopyProgram(sourceDir, InstallDir, copySettings);
                Protect(InstallDir);

                string bin = $"\"{InstalledExe}\" --service";
                IntPtr svc = OpenServiceW(scm, Name, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    svc = CreateServiceW(scm, Name, DisplayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                                         SERVICE_ERROR_NORMAL, bin, null, IntPtr.Zero, null, null, null); // 帳號 null = LocalSystem
                    if (svc == IntPtr.Zero) throw Error("建立系統服務失敗");
                }
                else if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, SERVICE_AUTO_START, SERVICE_NO_CHANGE, bin, null, IntPtr.Zero, null, null, null, DisplayName))
                    throw Error("更新系統服務設定失敗");
                try
                {
                    SetDescription(svc);
                    SetRestartOnFailure(svc);
                    if (!StartServiceW(svc, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != ERROR_SERVICE_ALREADY_RUNNING)
                        throw Error("啟動系統服務失敗");
                    WaitFor(svc, SERVICE_RUNNING, 15000);
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
            Logger.Info("系統服務已安裝並啟動");
        }

        /// <summary>停止並移除服務 (Program Files 裡的程式留著：系統匣程式正在從那裡執行)。本來就沒裝也算成功</summary>
        public static void Uninstall()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw Error("開啟服務管理員失敗");
            try
            {
                IntPtr svc = OpenServiceW(scm, Name, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) return;
                try
                {
                    StopAndWait(svc, 15000);
                    if (!DeleteService(svc) && Marshal.GetLastPInvokeError() != ERROR_SERVICE_MARKED_FOR_DELETE) throw Error("移除系統服務失敗");
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
            Logger.Info("系統服務已移除");
        }

        /// <summary>重新啟動服務 (更新換好檔案、改了設定檔之後)：遠端程式會用新的檔案 / 設定重新開</summary>
        public static void Restart()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw Error("開啟服務管理員失敗");
            try
            {
                IntPtr svc = OpenServiceW(scm, Name, SERVICE_START | SERVICE_STOP | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) throw Error("找不到系統服務");
                try
                {
                    StopAndWait(svc, 15000);
                    if (!StartServiceW(svc, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != ERROR_SERVICE_ALREADY_RUNNING)
                        throw Error("啟動系統服務失敗");
                    WaitFor(svc, SERVICE_RUNNING, 15000);
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
            Logger.Info("系統服務已重新啟動");
        }

        // exe、dll、設定檔複製過去；執行中的檔案 (更新後重裝) 不能覆蓋但可以改名，先改成 .old
        static void CopyProgram(string from, string to, bool copySettings)
        {
            Directory.CreateDirectory(to);
            if (string.Equals(Path.GetFullPath(from).TrimEnd('\\'), Path.GetFullPath(to).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            foreach (string src in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(src), ext = Path.GetExtension(src).ToLowerInvariant();
                bool settings = name.Equals("settings.json", StringComparison.OrdinalIgnoreCase);
                if (ext is not (".exe" or ".dll") && !settings) continue;
                string dst = Path.Combine(to, name);
                if (settings && !copySettings && File.Exists(dst)) continue;
                if (File.Exists(dst))
                {
                    try { File.Delete(dst); }
                    catch (Exception) { File.Move(dst, dst + ".old", true); }
                }
                File.Copy(src, dst);
            }
            // 連線紀錄搬過去 (之後都寫在新的位置)
            string record = Path.Combine(from, "logs", Path.GetFileName(RemoteLog.FilePath));
            string recordTo = Path.Combine(to, "logs", Path.GetFileName(RemoteLog.FilePath));
            if (File.Exists(record) && !File.Exists(recordTo))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(recordTo));
                File.Copy(record, recordTo);
            }
        }

        // 設定檔 (有房間碼、密碼) 只給系統管理員和 SYSTEM：Program Files 預設一般使用者也能讀。
        // 日誌不鎖 (只有房間碼與連線紀錄，沒有密碼)，系統匣「開啟日誌資料夾」用一般權限的檔案總管才打得開
        static void Protect(string dir)
        {
            string settings = Path.Combine(dir, "settings.json");
            if (!File.Exists(settings)) Settings.WriteExample(settings);
            Directory.CreateDirectory(Path.Combine(dir, "logs"));
            Icacls($"\"{settings}\" /inheritance:r /grant:r *S-1-5-32-544:F *S-1-5-18:F");
        }

        static void Icacls(string args)
        {
            var info = new ProcessStartInfo("icacls.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using Process p = Process.Start(info);
            string output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(15000);
            if (!p.HasExited || p.ExitCode != 0) throw new InvalidOperationException($"設定檔案權限失敗：{output}");
        }

        static void StopAndWait(IntPtr svc, int timeoutMs)
        {
            if (!QueryServiceStatus(svc, out SERVICE_STATUS st) || st.dwCurrentState == SERVICE_STOPPED) return;
            ControlService(svc, SERVICE_CONTROL_STOP, out _);
            WaitFor(svc, SERVICE_STOPPED, timeoutMs);
        }

        static void WaitFor(IntPtr svc, uint state, int timeoutMs)
        {
            long end = Environment.TickCount64 + timeoutMs;
            while (QueryServiceStatus(svc, out SERVICE_STATUS st) && st.dwCurrentState != state)
            {
                if (Environment.TickCount64 > end) throw new TimeoutException($"等系統服務{(state == SERVICE_RUNNING ? "啟動" : "停止")}逾時");
                if (state == SERVICE_RUNNING && st.dwCurrentState == SERVICE_STOPPED)
                    throw new InvalidOperationException($"系統服務啟動後馬上停止 (代碼 {st.dwWin32ExitCode})，請看日誌資料夾的 service_*.log");
                Thread.Sleep(200);
            }
        }

        static unsafe void SetDescription(IntPtr svc)
        {
            fixed (char* d = Description)
            {
                var info = new SERVICE_DESCRIPTIONW { lpDescription = d };
                ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, &info);
            }
        }

        // 服務意外結束時 5 秒後自動重開 (一天內不限次數)
        static unsafe void SetRestartOnFailure(IntPtr svc)
        {
            SC_ACTION* actions = stackalloc SC_ACTION[3];
            for (int i = 0; i < 3; i++) actions[i] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 5000 };
            var info = new SERVICE_FAILURE_ACTIONSW { dwResetPeriod = 86400, cActions = 3, lpsaActions = actions };
            ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, &info);
        }

        static Exception Error(string what)
        {
            int code = Marshal.GetLastPInvokeError();
            return new InvalidOperationException($"{what} (Windows 錯誤碼 {code})");
        }

        // ===== Win32 =====
        const uint SC_MANAGER_CONNECT = 0x1, SC_MANAGER_ALL_ACCESS = 0xF003F;
        const uint SERVICE_QUERY_STATUS = 0x4, SERVICE_START = 0x10, SERVICE_STOP = 0x20, SERVICE_ALL_ACCESS = 0xF01FF;
        const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 0x2, SERVICE_ERROR_NORMAL = 0x1, SERVICE_NO_CHANGE = 0xFFFFFFFF;
        const uint SERVICE_CONTROL_STOP = 0x1;
        const uint SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4, SERVICE_CONTINUE_PENDING = 5, SERVICE_PAUSE_PENDING = 6;
        const uint SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2, SC_ACTION_RESTART = 1;
        const int ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS { public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }
        [StructLayout(LayoutKind.Sequential)] unsafe struct SERVICE_DESCRIPTIONW { public char* lpDescription; }
        [StructLayout(LayoutKind.Sequential)] struct SC_ACTION { public uint Type, Delay; }
        [StructLayout(LayoutKind.Sequential)]
        unsafe struct SERVICE_FAILURE_ACTIONSW { public uint dwResetPeriod; public char* lpRebootMsg, lpCommand; public uint cActions; public SC_ACTION* lpsaActions; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateServiceW(IntPtr scm, string name, string displayName, uint access, uint type, uint start, uint error,
                                            string binaryPath, string loadOrderGroup, IntPtr tagId, string dependencies, string account, string password);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string binaryPath, string loadOrderGroup, IntPtr tagId,
                                                string dependencies, string account, string password, string displayName);
        [DllImport("advapi32.dll", SetLastError = true)] static extern unsafe bool ChangeServiceConfig2W(IntPtr svc, uint level, void* info);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartServiceW(IntPtr svc, uint argc, IntPtr argv);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr svc, uint control, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(IntPtr svc);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr handle);
    }
}
