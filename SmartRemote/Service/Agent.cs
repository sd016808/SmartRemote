using SmartRemote.Core;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartRemote.Service
{
    /// <summary>
    /// 遠端程式 (SmartRemote.exe --agent)：系統服務在主控台的工作階段以 SYSTEM 權限開的，沒有畫面。
    /// 做的事跟一般模式的系統匣程式一樣 (等觀看端、傳畫面、遙控)，差別是 SYSTEM 權限可以跟著切到安全桌面
    /// (UAC 確認視窗、鎖定畫面、登入畫面) 去截圖與送滑鼠鍵盤 (Native.FollowInputDesktop)。
    /// 系統匣程式透過 AgentPipe 看狀態、切換遙控 / 暫停。stdin 是服務給的：第一行隨機房間碼與密碼，之後 "exit" 或斷掉 = 結束
    /// </summary>
    static class Agent
    {
        public static int Run()
        {
            Logger.FilePrefix = "agent";
            var exit = new ManualResetEventSlim(false);
            string room = null, password = null;

            // 服務給的隨機碼 (只有服務開的才有 stdin；自己執行 --agent 測試時讀不到就自己產生)
            var stdin = new Thread(() =>
            {
                try
                {
                    using var reader = new StreamReader(Console.OpenStandardInput());
                    string first = reader.ReadLine();
                    if (first != null)
                    {
                        using var doc = JsonDocument.Parse(first);
                        room = doc.RootElement.GetProperty("room").GetString();
                        password = doc.RootElement.GetProperty("password").GetString();
                    }
                    codesReady.Set();
                    string line;
                    while ((line = reader.ReadLine()) != null && line != "exit") { }
                    Logger.Info(line == null ? "跟系統服務的連線斷了，結束" : "系統服務要求結束");
                }
                catch (Exception ex) { Logger.Info($"讀不到系統服務的指令 ({ex.Message})"); }
                finally
                {
                    codesReady.Set();
                    if (Environment.GetEnvironmentVariable("SMARTREMOTE_AGENT_NO_SERVICE") != "1") exit.Set();
                }
            }) { IsBackground = true, Name = "ServiceStdin" };
            stdin.Start();
            codesReady.Wait(3000);

            using var mutex = new Mutex(false, AgentPipe.TestMode ? @"Global\SmartRemote-Agent-Test" : @"Global\SmartRemote-Agent");
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(15)); } // 上一個遠端程式可能還在結束中
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { Logger.Warn("已經有另一個遠端程式在執行，結束"); return 3; }

            Logger.Info($"========== 遠端程式啟動 v{AutoUpdater.CurrentVersion} (工作階段 {System.Diagnostics.Process.GetCurrentProcess().SessionId}，{Environment.UserName}) ==========");
            if (!NativeLibrary.TryLoad("OpenCvSharpExtern", typeof(Agent).Assembly, null, out _))
            {
                Logger.Warn($"找不到 OpenCvSharpExtern.dll ({AppContext.BaseDirectory})");
                return 2;
            }
            Settings settings;
            try { settings = Settings.Load(); }
            catch (FormatException ex) { Logger.Warn(ex.Message); return 2; }

            room = settings.Room != "" ? settings.Room : room ?? RemoteView.RandomCode(8);
            password = settings.Password != "" ? settings.Password : password ?? RemoteView.RandomCode(10);
            RemoteFiles.CommonFolders = ConsoleUserFolders;

            AgentPipe.StartServer(room, password);
            RemoteView.Start(room, password, settings.Control, Environment.GetEnvironmentVariable("SMARTREMOTE_DRYRUN") == "1");
            exit.Wait();
            RemoteView.Stop("電腦端重新啟動了遠端服務");
            Logger.Info("遠端程式結束");
            return 0;
        }

        static readonly ManualResetEventSlim codesReady = new(false);

        // 傳檔案的常用資料夾：目前登入的使用者的 (SYSTEM 自己的是 C:\Windows\System32\config\systemprofile)；
        // 還沒有人登入 (登入畫面) 就只列磁碟
        static IEnumerable<(string Name, string Path)> ConsoleUserFolders()
        {
            var list = new List<(string, string)>();
            if (!WTSQueryUserToken(WTSGetActiveConsoleSessionId(), out IntPtr token)) return list;
            try
            {
                foreach (var (name, id) in new[] { ("桌面", FOLDERID_Desktop), ("下載", FOLDERID_Downloads), ("文件", FOLDERID_Documents), ("圖片", FOLDERID_Pictures) })
                {
                    if (SHGetKnownFolderPath(id, 0, token, out IntPtr path) == 0)
                    {
                        list.Add((name, Marshal.PtrToStringUni(path)));
                        Marshal.FreeCoTaskMem(path);
                    }
                }
            }
            finally { CloseHandle(token); }
            return list;
        }

        static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
        static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        static readonly Guid FOLDERID_Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        static readonly Guid FOLDERID_Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");

        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
