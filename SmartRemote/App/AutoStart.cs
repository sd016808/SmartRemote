using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace SmartRemote.Core
{
    /// <summary>
    /// 開機自動啟動 (使用者要求，設定檔 autoStart，預設不啟動)。程式要系統管理員權限才能控制鍵鼠，
    /// 登錄檔的 Run 不能啟動要系統管理員的程式 (Windows 登入時會擋掉)，所以用工作排程器：使用者登入時、以最高權限執行，開機不會跳 UAC。
    /// 排程的預設值會在用電池時不啟動、跑 3 天自動停止，這裡都關掉
    /// </summary>
    public static class AutoStart
    {
        const string TaskName = "SmartRemote";

        /// <summary>排程裡有沒有這個工作</summary>
        public static bool Exists() => Schtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

        /// <summary>建立 (或覆蓋) 排程：目前這個 exe、目前的使用者登入時以最高權限執行。成功回傳 true</summary>
        public static bool Enable()
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "SmartRemote_task.xml");
            try
            {
                // schtasks /XML 要 UTF-16 的檔案
                File.WriteAllText(xmlPath, TaskXml(Environment.ProcessPath, WindowsIdentity.GetCurrent().Name), System.Text.Encoding.Unicode);
                int code = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out string output);
                if (code != 0) Logger.Warn($"建立開機自動啟動排程失敗 (代碼 {code})：{output}");
                else Logger.Info($"已設定開機自動啟動 (工作排程器「{TaskName}」，最高權限)：{Environment.ProcessPath}");
                return code == 0;
            }
            catch (Exception ex)
            {
                Logger.Warn($"建立開機自動啟動排程失敗：{ex.Message}");
                return false;
            }
            finally { try { File.Delete(xmlPath); } catch { } }
        }

        /// <summary>刪掉排程 (本來就沒有也算成功)</summary>
        public static bool Disable()
        {
            if (!Exists()) return true;
            int code = Schtasks($"/Delete /TN \"{TaskName}\" /F", out string output);
            if (code != 0) Logger.Warn($"刪除開機自動啟動排程失敗 (代碼 {code})：{output}");
            else Logger.Info("已取消開機自動啟動");
            return code == 0;
        }

        /// <summary>
        /// 啟動時讓排程跟設定一致：要自動啟動就重建 (exe 搬過資料夾時路徑會更新)，不要就刪掉。
        /// 在背景執行 (schtasks 要零點幾秒)，不擋啟動
        /// </summary>
        public static void SyncInBackground(bool wanted) =>
            new Thread(() =>
            {
                try { if (wanted) Enable(); else Disable(); }
                catch (Exception ex) { Logger.Warn($"同步開機自動啟動失敗：{ex.Message}"); }
            }) { IsBackground = true, Name = "AutoStartSync" }.Start();

        // 純函式：排程的 XML (登入時觸發、互動式、最高權限、電池也跑、不限執行時間、已經在跑就不再開)
        internal static string TaskXml(string exePath, string user) => $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>SmartRemote 開機自動啟動 (以系統管理員權限執行，才能遙控滑鼠鍵盤)</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        static int Schtasks(string arguments, out string output)
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using Process p = Process.Start(info);
            output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
    }
}
