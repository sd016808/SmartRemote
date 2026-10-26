

namespace SmartRemote.Core
{
    /// <summary>
    /// 遠端畫面的連線紀錄：logs/remote_connections.log，誰 (來源位址、裝置) 在什麼時候連進來、連了多久、做了什麼 (操作、傳檔案)。
    /// 跟一般日誌分開、不會自動刪除 (使用者要求記錄連線時間與來源)
    /// </summary>
    public static class RemoteLog
    {
        static readonly object lockObj = new();
        public static string FilePath => AppPaths.InBaseDir(Path.Combine("logs", "remote_connections.log"));

        /// <summary>每寫一行就觸發 (介面用來跳出通知)；文字是 "觀看端 id  內容"</summary>
        public static event Action<string> Entry;

        public static void Write(string id, string text)
        {
            // 顯示用時間戳：用 DateTime.Now
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 觀看端 {id}  {text}";
            lock (lockObj)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch (Exception) { /* 寫不進去不能讓程式當掉 */ }
            }
            try { Entry?.Invoke(line); } catch { }
        }

        /// <summary>瀏覽器的 User-Agent 縮短成「Chrome / Android」這種</summary>
        public static string Device(string ua)
        {
            if (string.IsNullOrEmpty(ua)) return "不明 (舊版觀看頁)";
            string os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad"
                : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "其他系統";
            string browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("SamsungBrowser") ? "Samsung 瀏覽器"
                : ua.Contains("Firefox/") || ua.Contains("FxiOS") ? "Firefox" : ua.Contains("Chrome/") || ua.Contains("CriOS") ? "Chrome"
                : ua.Contains("Safari/") ? "Safari" : "其他瀏覽器";
            return $"{browser} / {os}";
        }

        /// <summary>觀看端連線碼 (SDP) 裡的外網位址 (STUN 查到的)；沒有的話是區網 (瀏覽器會把內網位址藏起來)</summary>
        public static string OfferSource(string sdp)
        {
            var addrs = sdp.Split('\n').Where(l => l.StartsWith("a=candidate:") && l.Contains(" typ srflx"))
                .Select(l => l.Split(' ')).Where(f => f.Length > 5).Select(f => f[4]).Distinct().ToList();
            return addrs.Count > 0 ? string.Join(", ", addrs) : "只有區網位址";
        }

        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小時 {t.Minutes} 分" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.Seconds} 秒";
    }
}
