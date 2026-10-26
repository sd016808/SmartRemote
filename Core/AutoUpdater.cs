using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartRemote.Core
{
    /// <summary>
    /// 自動更新 (不跳任何提示)：從公開的 GitHub Release (sd016808/SmartRemote) 查最新版，比目前新就下載 zip、換上新檔、重新啟動。
    /// - 程式啟動時檢查一次，另外可以從右鍵選單手動檢查；有人連線時不更新
    /// - 換檔方式：執行中的 exe / dll 不能覆蓋，但可以改名，所以舊檔改名成 .old，新檔放進來；失敗就把舊檔改回來
    /// - 查不到、網路錯誤一律略過，只寫日誌，不影響使用
    /// </summary>
    public static class AutoUpdater
    {
        const string ReleaseRepo = "sd016808/SmartRemote";
        const string OldSuffix = ".old";
        const string ExeName = "SmartRemote.exe";

        /// <summary>重新啟動時帶的參數：新程式看到它就等舊程式結束 (互斥鎖) 再啟動</summary>
        public const string AfterUpdateArg = "--after-update";

        public static readonly Version CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version;

        // 測試用環境變數：SMARTREMOTE_UPDATE_API 指到本機假的 API、SMARTREMOTE_UPDATE_INTERVAL_SEC 改檢查間隔
        static string LatestReleaseUrl => Environment.GetEnvironmentVariable("SMARTREMOTE_UPDATE_API")
                                          ?? $"https://api.github.com/repos/{ReleaseRepo}/releases/latest";

        static bool Disabled => Environment.GetEnvironmentVariable("SMARTREMOTE_DRYRUN") == "1"
                                || Environment.GetEnvironmentVariable("SMARTREMOTE_NO_UPDATE") == "1";

        public sealed record Pending(Version Version, string Url, string Name);

        /// <summary>查最新版；沒有更新、查不到、網路錯誤都回傳 null</summary>
        public static Pending Check()
        {
            if (Disabled) return null;
            try
            {
                using var http = NewHttp(TimeSpan.FromSeconds(10));
                string json = http.GetStringAsync(LatestReleaseUrl).GetAwaiter().GetResult();
                var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
                if (release == null || !TryParseVersion(release.TagName, out Version latest))
                    return null;
                if (latest <= CurrentVersion) return null;

                var zip = release.Assets?.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip?.DownloadUrl == null)
                {
                    Logger.Warn($"檢查更新：最新版 {release.TagName} 沒有 zip 檔，略過");
                    return null;
                }
                Logger.Info($"檢查更新：發現 v{latest} (目前 v{CurrentVersion})");
                return new Pending(latest, zip.DownloadUrl, zip.Name);
            }
            catch (Exception ex)
            {
                // 沒網路、GitHub 連不上、還沒有任何 Release (404) 都會到這裡
                Logger.Warn($"檢查更新失敗 (略過，不影響使用): {ex.Message}");
                return null;
            }
        }

        /// <summary>下載並換上新檔。失敗會把舊檔還原，回傳 false</summary>
        public static bool Install(Pending update)
        {
            string baseDir = AppContext.BaseDirectory;
            string tempDir = Path.Combine(Path.GetTempPath(), $"SmartRemoteUpdate_{update.Version}");
            string zipPath = tempDir + ".zip";
            var moved = new List<(string Dst, string Old)>();
            var added = new List<string>();
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (File.Exists(zipPath)) File.Delete(zipPath);

                using (var http = NewHttp(TimeSpan.FromMinutes(10)))
                using (var stream = http.GetStreamAsync(update.Url).GetAwaiter().GetResult())
                using (var file = File.Create(zipPath))
                    stream.CopyTo(file);
                ZipFile.ExtractToDirectory(zipPath, tempDir);

                // zip 裡如果多包了一層資料夾，以 SmartRemote.exe 所在的那層為根目錄
                string exe = Directory.GetFiles(tempDir, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                             ?? throw new InvalidOperationException($"更新檔裡找不到 {ExeName}");
                string root = Path.GetDirectoryName(exe);

                foreach (string src in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(baseDir, Path.GetRelativePath(root, src));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst))
                    {
                        string old = dst + OldSuffix;
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(dst, old);
                        moved.Add((dst, old));
                    }
                    File.Move(src, dst);
                    added.Add(dst);
                }

                Logger.Info($"已更新到 v{update.Version}：換上 {added.Count} 個檔案");
                try { File.Delete(zipPath); Directory.Delete(tempDir, true); } catch { /* 暫存檔刪不掉沒關係 */ }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"更新到 v{update.Version} 失敗，還原舊檔: {ex.Message}");
                foreach (string dst in added) { try { File.Delete(dst); } catch { } }
                foreach (var (dst, old) in moved) { try { File.Move(old, dst, true); } catch { } }
                return false;
            }
        }

        /// <summary>用新檔重新啟動 (帶 --after-update，讓新程式等舊程式結束)</summary>
        public static void Relaunch()
        {
            var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
            info.ArgumentList.Add(AfterUpdateArg);
            Process.Start(info);
        }

        /// <summary>刪掉上次更新留下的 .old (上次執行中的 exe / dll，現在已經沒在用了)</summary>
        public static void CleanupOldFiles()
        {
            try
            {
                foreach (string old in Directory.GetFiles(AppContext.BaseDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
                {
                    try { File.Delete(old); } catch { /* 可能舊程式還沒完全結束，下次再刪 */ }
                }
            }
            catch { }
        }

        static HttpClient NewHttp(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SmartRemote", CurrentVersion.ToString()));
            return http;
        }

        // "v1.0.2" / "1.0.2" → Version
        internal static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            return text != null && Version.TryParse(text.Trim().TrimStart('v', 'V'), out version);
        }
    }

    // GitHub「最新 Release」API 回傳的欄位 (只取用得到的)
    public sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new List<GitHubAsset>();
    }

    public sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; }
    }

    [JsonSerializable(typeof(GitHubRelease))]
    internal partial class UpdateJsonContext : JsonSerializerContext { }
}
