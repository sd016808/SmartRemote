using SmartRemote.Core;
using System.Diagnostics;

namespace SmartRemote.Service
{
    /// <summary>
    /// 系統服務模式的 Windows 防火牆允許規則。
    /// 一般模式第一次連線時 Windows 會跳「允許存取」讓使用者按，按了就有這個 exe 的允許規則；
    /// 系統服務的遠端程式是 SYSTEM 權限，Windows 不會跳，沒有規則的話別台電腦主動送進來的封包全被擋：
    /// 同一個區網的觀看端永遠連不上 (Chrome 把區網位址藏成 xxx.local，電腦要問 mDNS，回答也是送進來的封包；
    /// 觀看端送來的連線測試也進不來)，只有 4G 這種電腦先往外送、NAT 打洞的才連得上 (v1.3.10 以前的 bug)
    /// </summary>
    static class Firewall
    {
        public const string RuleName = "SmartRemote 遠端畫面 (系統服務)";

        /// <summary>系統服務啟動時在背景確認規則在不在，沒有就加。開機時防火牆服務可能還沒好，失敗就過一下再試</summary>
        public static void EnsureAllowedInBackground(string exe)
        {
            new Thread(() =>
            {
                for (int attempt = 1; ; attempt++)
                {
                    try { EnsureAllowed(exe); return; }
                    catch (Exception ex)
                    {
                        if (attempt >= 10)
                        {
                            Logger.Warn($"加入 Windows 防火牆規則失敗 (同一個區網的觀看端可能連不上)：{ex.Message}");
                            return;
                        }
                        if (attempt == 1) Logger.Info($"Windows 防火牆規則還沒加上 ({ex.Message})，15 秒後再試");
                        Thread.Sleep(15000);
                    }
                }
            }) { IsBackground = true, Name = "Firewall" }.Start();
        }

        static void EnsureAllowed(string exe)
        {
            // 已經有就不動 (使用者自己停用的話也尊重)
            if (Netsh($"advfirewall firewall show rule name=\"{RuleName}\"") == 0) return;
            int code = Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" " +
                             "protocol=udp profile=any enable=yes description=\"讓觀看端 (手機、別台電腦) 連得進來\"");
            if (code != 0) throw new InvalidOperationException($"netsh 代碼 {code}");
            Logger.Info($"已在 Windows 防火牆加入規則「{RuleName}」：允許 {exe} 接收連線 (UDP)");
        }

        // 只看結束代碼，輸出不要轉向：轉向要開可繼承的管道，同一時間服務開遠端程式 (CreateProcessAsUser 繼承 handle)
        // 的話，遠端程式會拿到管道的寫入端，netsh 結束了也讀不到結尾，這條執行緒就永遠卡住 (v1.3.11 的 bug：規則一直沒加上)
        static int Netsh(string arguments)
        {
            var info = new ProcessStartInfo("netsh.exe", arguments) { UseShellExecute = false, CreateNoWindow = true };
            using Process p = Process.Start(info);
            if (p.WaitForExit(15000)) return p.ExitCode;
            try { p.Kill(); } catch { }
            return -1;
        }
    }
}
