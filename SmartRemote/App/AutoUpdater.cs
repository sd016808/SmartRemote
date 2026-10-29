using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartRemote.Core
{
    /// <summary>
    /// 更新：從公開的 GitHub Release (sd016808/SmartRemote) 查最新版，比目前新就下載 zip、換上新檔、重新啟動。
    /// - 只有右鍵選單「檢查更新」時才檢查 (不會自己檢查)，有新版會先問要不要更新
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

        public sealed record Pending(Version Version, string Url, string Name, string Notes);

        /// <summary>查最新版：有新版回傳新版資訊；已是最新版回傳 null；查不到 (沒網路、GitHub 連不上…) 回傳 null 並給 error</summary>
        public static Pending Check(out string error)
        {
            error = null;
            if (Disabled)
            {
                error = "測試模式 (SMARTREMOTE_DRYRUN / SMARTREMOTE_NO_UPDATE) 不檢查更新";
                return null;
            }
            try
            {
                using var http = NewHttp(TimeSpan.FromSeconds(10));
                string json = http.GetStringAsync(LatestReleaseUrl).GetAwaiter().GetResult();
                var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
                if (release == null || !TryParseVersion(release.TagName, out Version latest))
                {
                    error = $"看不懂最新版的版本號 ({release?.TagName})";
                    return null;
                }
                if (latest <= CurrentVersion) return null;

                var zip = release.Assets?.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip?.DownloadUrl == null)
                {
                    Logger.Warn($"檢查更新：最新版 {release.TagName} 沒有 zip 檔，略過");
                    error = $"最新版 {release.TagName} 沒有安裝檔 (zip)";
                    return null;
                }
                Logger.Info($"檢查更新：發現 v{latest} (目前 v{CurrentVersion})");
                return new Pending(latest, zip.DownloadUrl, zip.Name, release.Body?.Trim());
            }
            catch (Exception ex)
            {
                // 沒網路、GitHub 連不上、還沒有任何 Release (404) 都會到這裡
                Logger.Warn($"檢查更新失敗: {ex.Message}");
                error = ex.Message;
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
            try { Process.Start(info); }
            catch (System.ComponentModel.Win32Exception)
            {
                // 新版要系統管理員權限、目前這個沒有 (從沒有要求權限的舊版更新上來)：改用系統提升，會跳一次 UAC
                Process.Start(new ProcessStartInfo(Environment.ProcessPath, AfterUpdateArg) { UseShellExecute = true, Verb = "runas" });
            }
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
        [JsonPropertyName("body")] public string Body { get; set; } // 更新說明
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
