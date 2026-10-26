namespace SmartRemote.Core
{
    /// <summary>日誌：exe 旁的 logs/ 資料夾，每天一個檔案 (smartremote_yyyyMMdd.log)，超過 14 天的舊檔自動刪除</summary>
    public static class Logger
    {
        const int KeepDays = 14;

        static readonly string LogDir = AppPaths.InBaseDir("logs");
        static readonly object lockObj = new object();
        static DateTime cleanedDate = DateTime.MinValue;

        public static event Action<string> Lines; // 給介面顯示最近的訊息用 (不一定有人訂閱)

        public static void Info(string message) => WriteLog("INFO", message);
        public static void Warn(string message) => WriteLog("WARN", message);

        static void WriteLog(string level, string message)
        {
            DateTime now = DateTime.Now;
            string logLine = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            lock (lockObj)
            {
                // 寫不進去就放棄這一行：日誌不能讓程式當掉
                try
                {
                    Directory.CreateDirectory(LogDir);
                    if (cleanedDate != now.Date) DeleteOldLogs(now.Date);
                    File.AppendAllText(Path.Combine(LogDir, $"smartremote_{now:yyyyMMdd}.log"), logLine + Environment.NewLine);
                }
                catch (Exception) { }
            }
            try { Lines?.Invoke(logLine); } catch { }
        }

        static void DeleteOldLogs(DateTime today)
        {
            cleanedDate = today;
            foreach (string file in Directory.GetFiles(LogDir, "smartremote_*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < today.AddDays(-KeepDays)) File.Delete(file);
                }
                catch (IOException) { /* 被其他程式開著就下次再刪 */ }
            }
        }
    }
}
