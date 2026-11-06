using SmartRemote.App;
using SmartRemote.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartRemote.Service
{
    /// <summary>
    /// 系統服務本體 (SmartRemote.exe --service，SYSTEM 權限、在看不到畫面的 session 0)。
    /// 自己不連線也不截圖，只負責在目前坐在電腦前的那個工作階段 (主控台) 開「遠端程式」(--agent，也是 SYSTEM 權限)：
    /// - 遠端程式結束 / 當掉就重開；換人登入 (切換使用者) 就關掉舊的、在新的工作階段開
    /// - 跟遠端程式之間用 stdin 的匿名管道：第一行給隨機的房間碼與密碼 (設定檔沒填時用，服務重新啟動前都一樣，
    ///   遠端程式重開也不會換)，之後送 "exit" 叫它結束；服務自己掛掉的話管道會斷，遠端程式也會跟著結束，不會留下沒人管的
    /// </summary>
    static unsafe class ServiceHost
    {
        static IntPtr statusHandle;
        static readonly ManualResetEvent stopRequested = new(false);
        static readonly AutoResetEvent sessionChanged = new(false);

        /// <summary>由服務管理員啟動時呼叫；不是的話 (直接執行) 回傳 1</summary>
        public static int Run()
        {
            Logger.FilePrefix = "service";
            IntPtr name = Marshal.StringToHGlobalUni(ServiceSetup.Name); // 程式結束前一直用
            var table = stackalloc SERVICE_TABLE_ENTRYW[2];
            table[0] = new SERVICE_TABLE_ENTRYW { lpServiceName = name, lpServiceProc = (IntPtr)(delegate* unmanaged<uint, IntPtr, void>)&ServiceMain };
            table[1] = default;
            if (!StartServiceCtrlDispatcherW(table))
            {
                Logger.Warn($"--service 只能由 Windows 服務管理員啟動 (錯誤碼 {Marshal.GetLastPInvokeError()})");
                return 1;
            }
            return 0;
        }

        [UnmanagedCallersOnly]
        static void ServiceMain(uint argc, IntPtr argv)
        {
            try
            {
                IntPtr name = Marshal.StringToHGlobalUni(ServiceSetup.Name);
                statusHandle = RegisterServiceCtrlHandlerExW(name, (IntPtr)(delegate* unmanaged<uint, uint, IntPtr, IntPtr, uint>)&Handler, IntPtr.Zero);
                SetState(SERVICE_RUNNING);
                Logger.Info($"========== 系統服務啟動 v{AutoUpdater.CurrentVersion} ==========");
                Firewall.EnsureAllowedInBackground(Environment.ProcessPath); // 安裝、更新後都會重新啟動服務，舊版裝的也會補上
                Supervise();
            }
            catch (Exception ex) { Logger.Warn($"系統服務出錯: {ex}"); }
            Logger.Info("系統服務停止");
            SetState(SERVICE_STOPPED);
        }

        [UnmanagedCallersOnly]
        static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
        {
            switch (control)
            {
                case SERVICE_CONTROL_STOP:
                case SERVICE_CONTROL_SHUTDOWN:
                    SetState(SERVICE_STOP_PENDING);
                    stopRequested.Set();
                    return 0;
                case SERVICE_CONTROL_SESSIONCHANGE:
                    sessionChanged.Set();
                    return 0;
                case SERVICE_CONTROL_INTERROGATE:
                    return 0;
            }
            return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }

        static void SetState(uint state)
        {
            var st = new SERVICE_STATUS
            {
                dwServiceType = SERVICE_WIN32_OWN_PROCESS,
                dwCurrentState = state,
                dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_SESSIONCHANGE : 0,
                dwWaitHint = state == SERVICE_STOP_PENDING ? 10000u : 0,
            };
            SetServiceStatus(statusHandle, &st);
        }

        // ===== 顧著遠端程式 =====
        sealed class AgentProcess
        {
            public IntPtr Process, StdinWrite;
            public uint Session, Pid;
            public long StartedAt;
            public bool Exited => WaitForSingleObject(Process, 0) == 0;
        }

        static void Supervise()
        {
            // 設定檔沒填房間碼 / 密碼時用的隨機碼：服務這次執行期間固定 (遠端程式重開、換人登入都不會變，觀看端才連得回來)
            string codes = RemoteView.Json(w => { w.WriteString("room", Settings.RandomRoom()); w.WriteString("password", Settings.RandomPassword()); });
            AgentProcess agent = null;
            int failures = 0;
            long retryAt = 0;
            while (!stopRequested.WaitOne(0))
            {
                uint session = WTSGetActiveConsoleSessionId();
                bool consoleOk = session != 0xFFFFFFFF && session != 0;
                if (agent != null && (agent.Exited || !consoleOk || agent.Session != session))
                {
                    if (agent.Exited)
                    {
                        GetExitCodeProcess(agent.Process, out uint code);
                        // 很快就結束 (設定檔錯誤、缺檔案…) 就越等越久再重開，不要一直重試
                        failures = Environment.TickCount64 - agent.StartedAt < 30000 ? failures + 1 : 0;
                        retryAt = Environment.TickCount64 + Math.Min(60000, 2000 * (1 << Math.Min(failures, 5)));
                        Logger.Warn($"遠端程式 (工作階段 {agent.Session}，PID {agent.Pid}) 結束了 (代碼 {code})");
                    }
                    else Logger.Info($"主控台換到工作階段 {session}，關掉工作階段 {agent.Session} 的遠端程式");
                    StopAgent(agent);
                    agent = null;
                }
                if (agent == null && consoleOk && Environment.TickCount64 >= retryAt)
                {
                    try { agent = StartAgent(session, codes); }
                    catch (Exception ex)
                    {
                        Logger.Warn($"在工作階段 {session} 開遠端程式失敗: {ex.Message}");
                        retryAt = Environment.TickCount64 + 10000;
                    }
                }
                var waits = new List<WaitHandle> { stopRequested, sessionChanged };
                ProcessWait handle = agent != null ? new ProcessWait(agent.Process) : null;
                if (handle != null) waits.Add(handle);
                WaitHandle.WaitAny(waits.ToArray(), 2000);
            }
            if (agent != null) StopAgent(agent);
        }

        // 程序結束時觸發的 WaitHandle (不擁有 handle)
        sealed class ProcessWait : WaitHandle
        {
            public ProcessWait(IntPtr process) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process, false);
        }

        // 複製服務自己的 SYSTEM 權杖、改成目標工作階段，在那個工作階段的一般桌面開 --agent
        static AgentProcess StartAgent(uint session, string codes)
        {
            IntPtr token = IntPtr.Zero, primary = IntPtr.Zero, read = IntPtr.Zero, write = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out token)) throw Error("OpenProcessToken");
                if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, 2, 1, out primary)) throw Error("DuplicateTokenEx"); // SecurityImpersonation、TokenPrimary
                if (!SetTokenInformation(primary, 12, &session, 4)) throw Error("SetTokenInformation(TokenSessionId)");

                var sa = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
                if (!CreatePipe(out read, out write, &sa, 0)) throw Error("CreatePipe");
                SetHandleInformation(write, 1, 0); // 寫的那一端不給子程序

                string exe = Environment.ProcessPath;
                char[] cmd = ($"\"{exe}\" --agent\0").ToCharArray();
                fixed (char* desktop = "winsta0\\default")
                fixed (char* cmdLine = cmd)
                {
                    var si = new STARTUPINFOW { cb = sizeof(STARTUPINFOW), lpDesktop = desktop, dwFlags = STARTF_USESTDHANDLES, hStdInput = read };
                    if (!CreateProcessAsUserW(primary, exe, cmdLine, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                                              IntPtr.Zero, Path.GetDirectoryName(exe), &si, out PROCESS_INFORMATION pi))
                        throw Error("CreateProcessAsUser");
                    CloseHandle(pi.hThread);
                    CloseHandle(read);
                    read = IntPtr.Zero;
                    var agent = new AgentProcess { Process = pi.hProcess, StdinWrite = write, Session = session, Pid = pi.dwProcessId, StartedAt = Environment.TickCount64 };
                    write = IntPtr.Zero;
                    Send(agent, codes);
                    Logger.Info($"在工作階段 {session} 開了遠端程式 (PID {pi.dwProcessId})");
                    return agent;
                }
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (read != IntPtr.Zero) CloseHandle(read);
                if (write != IntPtr.Zero) CloseHandle(write);
            }
        }

        static void Send(AgentProcess agent, string line)
        {
            if (agent.StdinWrite == IntPtr.Zero) return;
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            fixed (byte* p = data) WriteFile(agent.StdinWrite, p, (uint)data.Length, out _, IntPtr.Zero);
        }

        // 叫遠端程式結束 (它會先告訴觀看端)，等 5 秒還沒結束就直接結束掉
        static void StopAgent(AgentProcess agent)
        {
            if (!agent.Exited)
            {
                Send(agent, "exit");
                if (WaitForSingleObject(agent.Process, 5000) != 0)
                {
                    Logger.Warn($"遠端程式 (PID {agent.Pid}) 沒有自己結束，強制結束");
                    TerminateProcess(agent.Process, 1);
                    WaitForSingleObject(agent.Process, 3000);
                }
            }
            CloseHandle(agent.StdinWrite);
            CloseHandle(agent.Process);
        }

        static Exception Error(string what) => new InvalidOperationException($"{what} 失敗 (錯誤碼 {Marshal.GetLastPInvokeError()})");

        // ===== Win32 =====
        const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_STOPPED = 1, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
        const uint SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_SHUTDOWN = 0x4, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
        const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5, SERVICE_CONTROL_SESSIONCHANGE = 0xE;
        const uint TOKEN_ALL_ACCESS = 0xF01FF, MAXIMUM_ALLOWED = 0x02000000;
        const uint STARTF_USESTDHANDLES = 0x100, CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x400;

        [StructLayout(LayoutKind.Sequential)] struct SERVICE_TABLE_ENTRYW { public IntPtr lpServiceName, lpServiceProc; }
        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS { public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }
        [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOW
        {
            public int cb;
            public char* lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

        [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW* table);
        [DllImport("advapi32.dll", SetLastError = true)] static extern IntPtr RegisterServiceCtrlHandlerExW(IntPtr name, IntPtr handler, IntPtr context);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceStatus(IntPtr handle, SERVICE_STATUS* status);
        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, void* info, uint length);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUserW(IntPtr token, string application, char* commandLine, IntPtr processAttributes, IntPtr threadAttributes,
                                                bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, STARTUPINFOW* startup, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out IntPtr read, out IntPtr write, SECURITY_ATTRIBUTES* attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr file, byte* buffer, uint length, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    }
}
