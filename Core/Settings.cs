using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartRemote.Core
{
    /// <summary>
    /// 設定檔 settings.json (跟 exe 放在同一層)：
    /// room / password 不填 = 每次啟動隨機產生；填了就固定 (書籤存一次就好)
    /// control：false = 只能看；true = 還能遙控這台電腦 (滑鼠、鍵盤、剪貼簿、傳檔案)
    /// </summary>
    public sealed class Settings
    {
        [JsonPropertyName("room")] public string Room { get; set; } = "";
        [JsonPropertyName("password")] public string Password { get; set; } = "";
        [JsonPropertyName("control")] public bool Control { get; set; }

        public static string FilePath => AppPaths.InBaseDir("settings.json");

        const string Example = """
            {
              // 房間碼 / 密碼：不填 = 每次啟動隨機產生；填了就固定 (英文字母或數字，房間碼至少 4 個字、密碼至少 8 個字)
              "room": "",
              "password": "",
              // false = 只能看；true = 可以遙控這台電腦 (知道房間碼和密碼的人就能操作，密碼請設長一點)
              "control": false
            }
            """;

        /// <summary>讀取設定；沒有檔案就建立範例。格式錯誤 / 房間碼密碼不合規定 → 丟 FormatException (訊息給使用者看)</summary>
        public static Settings Load()
        {
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, Example);
            Settings s;
            try
            {
                s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new Settings();
            }
            catch (JsonException ex) { throw new FormatException($"settings.json 格式錯誤：{ex.Message}"); }
            s.Room = Code(s.Room, "room", 4);
            s.Password = Code(s.Password, "password", 8);
            return s;
        }

        // 轉大寫；只能英數 - _ (觀看頁網址 #房間碼/密碼 用到)
        static string Code(string value, string name, int min)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim().ToUpperInvariant();
            if (value.Length < min || !value.All(ch => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
                throw new FormatException($"settings.json：{name} 要是至少 {min} 個字的英文字母或數字 (不填 = 每次啟動隨機產生)");
            return value;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
    [JsonSerializable(typeof(Settings))]
    internal partial class SettingsJson : JsonSerializerContext { }
}
