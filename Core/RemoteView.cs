
using SIPSorcery.Net;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SmartRemote.Core
{
    /// <summary>
    /// 遠端畫面：手機 / 別台電腦用瀏覽器開觀看頁，輸入房間碼與密碼就能看這台的螢幕；設定檔 control 開了還能遙控
    /// (滑鼠、鍵盤、剪貼簿、傳檔案)。
    /// - 連線碼 (WebRTC 的 SDP) 透過 ntfy.sh 交換：信箱名稱由房間碼算出，內容用房間碼 + 密碼算出的金鑰 AES-GCM 加密，ntfy 看不到內容
    /// - 畫面本身點對點直連 (STUN 打洞，沒有 TURN 中繼)：雙方網路都不允許直連時會連不上
    /// - 每個觀看端一個 RemoteSession (畫面、遙控、傳檔案)；連線紀錄寫在 logs/remote_connections.log (RemoteLog)
    /// 觀看頁原始碼在 Docs/RemoteViewer.html，放在公開 repo 的 GitHub Pages (ViewerUrl)；協定改了兩邊要一起改，而且新舊版要能互連
    /// </summary>
    public static class RemoteView
    {
        public const string ViewerUrl = "https://sd016808.github.io/SmartRemote/";
        const string Ntfy = "https://ntfy.sh";
        const string Stun = "stun:stun.l.google.com:19302";
        const int ConnectTimeoutMs = 30000;
        const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 隨機碼不用 0/O、1/I 這種容易抄錯的

        static readonly HttpClient http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        static string topic;
        static byte[] key;
        static volatile bool allowControl;
        static bool dryRun;
        static volatile bool paused;
        static readonly List<RemoteSession> sessions = new();

        /// <summary>連線數、暫停、遙控開關等狀態改變時觸發 (介面據此更新)</summary>
        public static event Action StateChanged;

        public static bool Paused => paused;
        public static bool AllowControl => allowControl;
        public static int Connections { get { lock (sessions) return sessions.Count(s => s.Connected); } }

        /// <summary>隨機產生的房間碼 / 密碼 (設定檔沒填時用)</summary>
        public static string RandomCode(int length) => new string(RandomNumberGenerator.GetItems<char>(CodeChars, length));

        /// <summary>
        /// 開始在背景等觀看端連線 (room / password 已轉成大寫)；control = 允許遙控。
        /// dryRun (--test-remote 的 dry)：遙控的輸入、剪貼簿只寫日誌不真的動作
        /// </summary>
        public static void Start(string room, string password, bool control, bool dryRun = false)
        {
            topic = "lash-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("lash-topic:" + room))).ToLowerInvariant()[..24];
            key = SHA256.HashData(Encoding.UTF8.GetBytes("lash-key:" + room + ":" + password));
            allowControl = control;
            RemoteView.dryRun = dryRun;
            RemoteClipboard.DryRun = dryRun;
            Logger.Info($"[遠端畫面] 開始等待觀看端 (房間碼 {room}，{(control ? "可遙控" : "只能看")})");
            _ = Task.Run(Listen);
        }

        /// <summary>本機一鍵中斷 (Ctrl+F7)：中斷所有觀看端並暫停接受新連線，再按一次恢復。回傳 true = 現在是暫停中</summary>
        public static bool TogglePause()
        {
            paused = !paused;
            if (paused)
            {
                List<RemoteSession> all;
                lock (sessions) all = sessions.ToList();
                foreach (var s in all) s.Close("電腦端按了中斷", bye: "電腦端中斷了連線，並暫停接受新的連線");
            }
            Logger.Info($"[遠端畫面] {(paused ? "電腦端中斷所有連線，暫停接受新連線" : "恢復接受連線")}");
            StateChanged?.Invoke();
            return paused;
        }

        /// <summary>
        /// 執行中改遙控開關：關掉之後已連線的觀看端也不能再遙控 (畫面照樣看)；開啟只影響之後的連線
        /// </summary>
        public static void SetControl(bool on)
        {
            allowControl = on;
            List<RemoteSession> all;
            lock (sessions) all = sessions.ToList();
            if (!on) foreach (var s in all) s.DisableControl();
            Logger.Info($"[遠端畫面] 遙控{(on ? "開啟" : "關閉")}");
            StateChanged?.Invoke();
        }

        /// <summary>目前的連線狀態文字 (給介面與 log 用)</summary>
        public static string Status
        {
            get
            {
                if (paused) return "暫停接受連線";
                int n, control;
                lock (sessions) { n = sessions.Count(s => s.Connected); control = sessions.Count(s => s.Connected && s.Control); }
                if (n == 0) return "等待連線";
                return control > 0 ? $"{n} 個連線，可遙控" : $"{n} 個連線觀看中";
            }
        }

        internal static void StatusChanged() => StateChanged?.Invoke();

        internal static void Remove(RemoteSession session)
        {
            lock (sessions) sessions.Remove(session);
            StateChanged?.Invoke();
        }

        // 訂閱 ntfy (串流 JSON，一行一則)；斷線就重連
        static async Task Listen()
        {
            while (true)
            {
                try
                {
                    using var resp = await http.GetAsync($"{Ntfy}/{topic}/json", HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.GetProperty("event").GetString() != "message") continue;
                        string msg = doc.RootElement.GetProperty("message").GetString();
                        _ = Task.Run(() => HandleMessage(msg));
                    }
                }
                catch (Exception ex) { Logger.Warn($"[遠端畫面] ntfy 連線中斷，10 秒後重連: {ex.Message}"); }
                await Task.Delay(10000);
            }
        }

        static async Task HandleMessage(string msg)
        {
            string id, sdp, ua;
            int version;
            try
            {
                using var doc = JsonDocument.Parse(Decrypt(msg));
                var root = doc.RootElement;
                if (root.GetProperty("t").GetString() != "offer") return;
                id = root.GetProperty("id").GetString();
                sdp = root.GetProperty("sdp").GetString();
                // 新版觀看頁 (v2) 才會帶版本與瀏覽器；舊版只能看 JPEG、不能遙控
                version = root.TryGetProperty("v", out var v) ? v.GetInt32() : 1;
                ua = root.TryGetProperty("ua", out var u) ? u.GetString() : null;
            }
            catch { return; }   // 解不開 = 密碼不對 / 不是觀看端的訊息 (自己送出的回覆也會收到)
            Logger.Info($"[遠端畫面] 觀看端 {id} 要求連線 ({ua ?? "舊版觀看頁"})");
            RemoteLog.Write(id, $"要求連線  來源 {RemoteLog.OfferSource(sdp)}  裝置 {RemoteLog.Device(ua)}");
            if (paused)
            {
                RemoteLog.Write(id, "拒絕 (電腦端暫停接受連線)");
                return;
            }

            RTCPeerConnection pc = null;
            RemoteSession session = null;
            try
            {
                pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer> { new RTCIceServer { urls = Stun } } });
                session = new RemoteSession(pc, id, version >= 2, allowControl, dryRun);
                lock (sessions) sessions.Add(session);
                var gathered = new TaskCompletionSource();
                pc.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };
                session.AddVideoTrack(sdp);
                var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
                if (result != SetDescriptionResultEnum.OK) { Logger.Warn($"[遠端畫面] 觀看端 {id} 的連線碼無效: {result}"); session.Close($"連線碼無效 {result}"); return; }
                // 一定要等位址收集完 (STUN 查到外網位址) 才建立回覆：回覆裡的位址在 createAnswer 當下就固定了。
                // 先回覆再等的話，只有程式剛啟動、第一次比較慢時碰巧來得及，之後的回覆都沒有外網位址，只有同一個區網連得上 (v1.15.0 的 bug)
                if (pc.iceGatheringState != RTCIceGatheringState.complete) await Task.WhenAny(gathered.Task, Task.Delay(5000));
                await pc.setLocalDescription(pc.createAnswer(null));
                string sdpOut = pc.localDescription.sdp.ToString();
                session.CheckAnswer(sdpOut);
                if (!sdpOut.Contains(" typ srflx")) Logger.Warn($"[遠端畫面] 觀看端 {id}：查不到外網位址 (STUN 沒回應)，只有同一個區網連得上");
                await Publish(Encrypt(Json(w =>
                {
                    w.WriteString("t", "answer");
                    w.WriteString("id", id);
                    w.WriteString("sdp", sdpOut);
                })));
                // 一直沒連上 (例如雙方網路都不能直連) 就收掉
                await Task.Delay(ConnectTimeoutMs);
                if (pc.connectionState != RTCPeerConnectionState.connected) session.Close("逾時沒有連上");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[遠端畫面] 觀看端 {id} 連線失敗: {ex.Message}");
                if (session != null) session.Close($"錯誤：{ex.Message}");
                else pc?.close();
            }
        }

        // 中文不轉成 \uXXXX (剪貼簿、檔名比較短；DataChannel 單則上限 256 KB)
        static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        internal static string Json(Action<Utf8JsonWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, JsonOptions))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static async Task Publish(string text)
        {
            using var content = new StringContent(text, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            (await http.PostAsync($"{Ntfy}/{topic}", content)).EnsureSuccessStatusCode();
        }

        // 跟觀看頁相同：deflate-raw 壓縮 (ntfy 單則超過 4096 bytes 會變成附件) → AES-GCM → base64(iv 12 | 密文 | tag 16)
        static string Encrypt(string json)
        {
            byte[] plain;
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal)) z.Write(Encoding.UTF8.GetBytes(json));
                plain = ms.ToArray();
            }
            byte[] iv = RandomNumberGenerator.GetBytes(12), ct = new byte[plain.Length], tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, plain, ct, tag);
            return Convert.ToBase64String([.. iv, .. ct, .. tag]);
        }

        static string Decrypt(string b64)
        {
            byte[] all = Convert.FromBase64String(b64.Trim());
            byte[] plain = new byte[all.Length - 28];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(all[..12], all[12..^16], all[^16..], plain);
            using var z = new DeflateStream(new MemoryStream(plain), CompressionMode.Decompress);
            using var sr = new StreamReader(z, Encoding.UTF8);
            return sr.ReadToEnd();
        }
    }
}
