
using OpenCvSharp;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Diagnostics;
using System.Text.Json;

namespace SmartRemote.Core
{
    /// <summary>
    /// 一個觀看端的連線：
    /// - 畫面：觀看頁有要影像軌 (新版) 而且這台能用 H.264 編碼 → WebRTC 影像軌；否則跟舊版一樣 JPEG 從 DataChannel 送
    /// - "screen" DataChannel (觀看端建立)：選螢幕 / 畫質、遙控的滑鼠鍵盤、剪貼簿；分享端送螢幕清單、能力 (caps)、提示 (note)、中斷 (bye)
    /// - "files" DataChannel：傳檔案 (RemoteFiles)
    /// 遙控、剪貼簿、傳檔案都只有設定檔 control 開了才處理
    /// </summary>
    sealed class RemoteSession
    {
        // 畫質：觀看頁可以選 (省流量 / 標準 / 清晰)；都是 60 張/秒，只差解析度與流量 (使用者要求全速、而且不要分得讓人看不懂)
        sealed record Quality(int MaxWidth, int Bitrate, int Fps, int JpegQuality, int JpegMs);
        static readonly Quality[] Qualities =
        {
            new(1280, 2_500_000, 60, 50, 150),
            new(1920, 6_000_000, 60, 60, 120),
            new(int.MaxValue, 20_000_000, 60, 80, 100),
        };
        static readonly Quality OldViewer = new(1280, 0, 3, 55, 330); // 舊版觀看頁：跟 v1.15 一樣

        // 自動畫質 (觀看頁的 quality = 3)：觀看頁看網路狀況送來碼率 ({"t":"rate","b":...})，解析度跟張數跟著碼率走。
        // 碼率低時縮小畫面 (同樣碼率，畫面小一點反而清楚)；很低時改每秒 30 張
        const int AutoQuality = 3, AutoMinBitrate = 500_000, AutoMaxBitrate = 40_000_000; // 上限 40 Mbps：觀看頁在區網 (來回時間很短) 才會要到 20 以上
        static readonly int[] AutoWidths = { 1280, 1920, int.MaxValue };
        static readonly int[] AutoDown = { 0, 2_500_000, 6_000_000 }; // 碼率低於這個就降一級解析度
        static readonly int[] AutoUp = { 0, 3_500_000, 9_000_000 };   // 碼率到這個才升一級 (跟 AutoDown 錯開，避免來回切)
        volatile int autoBitrate = 10_000_000, autoTier = 2;
        long autoTierAt;

        // 觀看頁畫面實際畫在幾個裝置像素上 ({"t":"view","w":...}，0 / 沒送 = 不限制)：比這個大的送過去也只是被瀏覽器縮小
        // (瀏覽器縮影片用最簡單的雙線性，細字會粗細不均)；這邊用面積平均縮好、觀看頁 1:1 顯示，字比較清楚，
        // 像素少了同樣流量畫質也比較好。舊版觀看頁不會送 = 跟以前一樣
        volatile int viewWidth = int.MaxValue;

        // 這次要送多寬：畫質的上限、觀看頁顯示的寬度取小的；顯示寬度跟原尺寸差不多 (97% 以上) 就直接送原尺寸
        int MaxWidth(Quality q, int sourceWidth)
        {
            int view = viewWidth;
            return Math.Min(q.MaxWidth, view >= sourceWidth - sourceWidth / 32 ? int.MaxValue : view);
        }

        public string Id { get; }
        public bool Control { get; private set; }
        public bool Connected => connectedAt != null;

        readonly RTCPeerConnection pc;
        readonly bool newViewer, dryRun;
        /// <summary>這個觀看端的影像軌 (協商、送封包、重送)；RemoteView 建立連線時用到協商的部分</summary>
        public VideoSender Video { get; }
        bool video => Video.Negotiated; // 影像軌協商成功 (否則用 JPEG)
        RTCDataChannel main;
        RemoteInput input;
        volatile List<Native.Monitor> monitors = Native.Monitors(); // 整份換掉、不改內容 (遙控、訊息的執行緒也在讀)
        volatile int screen = -1, quality = 1, closed;
        volatile bool forceFrame = true;
        volatile bool wantHardware = true; // 觀看頁選的編碼器：true = 優先用顯示卡硬體編碼 (預設)，false = 軟體編碼
        DateTime? connectedAt;
        string note;
        readonly Action<string> onClipboard;

        public RemoteSession(RTCPeerConnection pc, string id, bool newViewer, bool control, bool dryRun)
        {
            this.pc = pc;
            Id = id;
            this.newViewer = newViewer;
            Control = control && newViewer;
            this.dryRun = dryRun;
            onClipboard = text => SendJson(w => { w.WriteString("t", "clip"); w.WriteString("s", text); });
            Video = new VideoSender(pc, id, () => CurrentQuality(quality).Bitrate);

            pc.onconnectionstatechange += s =>
            {
                Logger.Info($"[遠端畫面] 觀看端 {id}：{s}");
                if (s == RTCPeerConnectionState.connected && connectedAt == null)
                {
                    connectedAt = DateTime.Now; // 顯示用時間
                    Video.WatchFeedback();
                    CheckSameComputer();
                    RemoteLog.Write(id, $"連線成功  來源 {Source()}  模式 {(Control ? "可遙控" : "只能看")}");
                    RemoteView.StatusChanged();
                }
                if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
                    Close(s == RTCPeerConnectionState.closed ? "連線關閉" : "連線中斷");
            };
            pc.ondatachannel += dc =>
            {
                if (dc.label == "files")
                {
                    if (Control) new RemoteFiles(dc, id, text => RemoteLog.Write(id, text));
                    else dc.close();
                    return;
                }
                OnMainChannel(dc);
            };
        }

        // ===== 主要的 DataChannel =====
        void OnMainChannel(RTCDataChannel dc)
        {
            main = dc;
            dc.onmessage += (_, protocol, data) =>
            {
                if (protocol is not (DataChannelPayloadProtocols.WebRTC_String or DataChannelPayloadProtocols.WebRTC_String_Partial)) return;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    OnMessage(doc.RootElement);
                }
                catch (Exception ex) { Logger.Warn($"[遠端畫面] 觀看端 {Id} 訊息處理失敗: {ex.Message}"); }
            };
            dc.onclose += () => Close("觀看端關閉");
            int started = 0;
            void Begin()
            {
                if (Interlocked.Exchange(ref started, 1) == 1) return;
                CheckSameComputer();
                if (Control) input = new RemoteInput(Id, dryRun);
                SendCaps();
                SendScreens(-1);
                if (Control) RemoteClipboard.Watch(onClipboard);
                Logger.Info($"[遠端畫面] 觀看端 {Id} 開始傳送畫面 ({monitors.Count} 個螢幕，{(video ? "H.264 影像" : "JPEG")}，{(Control ? "可遙控" : "只能看")})");
                new Thread(SendFrames) { IsBackground = true, Name = "RemoteVideo-" + Id }.Start();
            }
            dc.onopen += Begin;
            if (dc.readyState == RTCDataChannelState.open) Begin();
        }

        // 螢幕清單：{"t":"screens","list":[{"i":0,"w":2560,"h":1440,"primary":true}, ...]}；
        // 螢幕有變動時再送一次，cur = 電腦端現在看的是哪一個 (觀看頁照這個選，不要跳到別的螢幕；舊版觀看頁不看)
        void SendScreens(int cur)
        {
            var list = monitors;
            SendJson(w =>
            {
                w.WriteString("t", "screens");
                w.WriteStartArray("list");
                for (int i = 0; i < list.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("i", i);
                    w.WriteNumber("w", list[i].Bounds.Width);
                    w.WriteNumber("h", list[i].Bounds.Height);
                    w.WriteBoolean("primary", list[i].Primary);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (cur >= 0) w.WriteNumber("cur", cur);
            });
        }

        /// <summary>
        /// 螢幕拔掉 / 接上 / 進入省電斷線 / 改解析度之後，Windows 給每個螢幕的代號 (HMONITOR) 會換掉，
        /// 拿舊的代號截圖會一直「找不到這個螢幕對應的顯示卡輸出」。重新列一次，有變就換成新的清單，
        /// 盡量留在同一個實體螢幕 (位置大小一樣的)，並告訴觀看頁
        /// </summary>
        void RefreshMonitors()
        {
            var now = Native.Monitors();
            var old = monitors;
            if (now.Count == old.Count && now.Zip(old).All(p => p.First == p.Second)) return;
            Rect was = Monitor().Bounds;
            int keep = now.FindIndex(m => m.Bounds == was);
            monitors = now;
            screen = keep; // 找不到 (那個螢幕不見了) = -1 = 主螢幕
            Logger.Info($"[遠端畫面] 觀看端 {Id}：螢幕變了 ({old.Count} → {now.Count} 個)，{(keep >= 0 ? $"繼續看螢幕 {keep + 1}" : "改看主螢幕")}");
            SendScreens(now.IndexOf(Monitor()));
        }

        // 能力 (新版觀看頁看這個決定要顯示哪些功能；舊版會忽略)：hw = 這台有顯示卡硬體編碼器，觀看頁才顯示硬體 / 軟體編碼的切換
        void SendCaps() => SendJson(w =>
        {
            w.WriteString("t", "caps");
            w.WriteNumber("v", 2);
            w.WriteBoolean("video", video);
            w.WriteBoolean("control", Control);
            w.WriteBoolean("hw", video && H264Encoder.HasHardware);
            w.WriteBoolean("auto", video);
        });

        // 現在要用的畫質：舊版觀看頁固定；自動 = 觀看頁送來的碼率加上對應的解析度 / 張數
        Quality CurrentQuality(int qi)
        {
            if (!newViewer) return OldViewer;
            if (qi != AutoQuality) return Qualities[qi];
            int b = autoBitrate;
            return new Quality(AutoWidths[autoTier], b, b < 1_500_000 ? 30 : 60, 60, 120);
        }

        // 解析度往下馬上換；往上至少隔 10 秒 (每換一次都要重建編碼器、重送關鍵畫格，不要來回切)
        void SetAutoBitrate(int bitrate)
        {
            int b = Math.Clamp(bitrate, AutoMinBitrate, AutoMaxBitrate), tier = autoTier, want = tier;
            while (want > 0 && b < AutoDown[want]) want--;
            while (want < AutoWidths.Length - 1 && b >= AutoUp[want + 1]) want++;
            long now = Environment.TickCount64;
            if (want < tier || (want > tier && now - autoTierAt >= 10_000))
            {
                autoTier = want;
                autoTierAt = now;
                Logger.Info($"[遠端畫面] 觀看端 {Id} 自動畫質：{(AutoWidths[want] == int.MaxValue ? "原始解析度" : $"寬 {AutoWidths[want]}")}，{b / 1_000_000.0:0.0} Mbps");
            }
            autoBitrate = b;
        }

        void OnMessage(JsonElement m)
        {
            string t = m.GetProperty("t").GetString();
            switch (t)
            {
                case "screen":
                    screen = m.GetProperty("i").GetInt32();
                    return;
                case "quality":
                    quality = Math.Clamp(m.GetProperty("q").GetInt32(), 0, AutoQuality);
                    return;
                case "rate": // 自動畫質：觀看頁算出來的碼率
                    SetAutoBitrate(m.GetProperty("b").GetInt32());
                    return;
                case "ping": // 觀看頁量來回時間 (自動畫質用)：原樣送回去
                    double ts = m.GetProperty("ts").GetDouble();
                    SendJson(w => { w.WriteString("t", "pong"); w.WriteNumber("ts", ts); });
                    return;
                case "encoder": // 觀看頁選硬體 / 軟體編碼
                    wantHardware = m.GetProperty("hw").GetBoolean();
                    return;
                case "view": // 觀看頁畫面顯示的寬度 (裝置像素)；0 = 不限制 (原始大小)
                    int vw = m.TryGetProperty("w", out var wv) ? wv.GetInt32() : 0;
                    int was = viewWidth;
                    viewWidth = vw <= 0 ? int.MaxValue : Math.Max(vw, 320);
                    if (viewWidth != was) Logger.Info($"[遠端畫面] 觀看端 {Id} 顯示寬度 {(vw <= 0 ? "不限制" : viewWidth.ToString())}");
                    return;
            }
            if (!Control || input == null) return; // 只能看：遙控的訊息一律不理
            switch (t)
            {
                case "mm": input.Move(Bounds(), X(m), Y(m)); break;
                case "md": input.Button(Bounds(), X(m), Y(m), m.GetProperty("b").GetInt32(), true); break;
                case "mu": input.Button(Bounds(), X(m), Y(m), m.GetProperty("b").GetInt32(), false); break;
                case "wh": input.Wheel(Int(m, "dx"), Int(m, "dy")); break;
                case "kd": input.Key(m.GetProperty("c").GetString(), true); break;
                case "ku": input.Key(m.GetProperty("c").GetString(), false); break;
                case "keys": input.Keys(m.GetProperty("c").EnumerateArray().Select(e => e.GetString()).Where(RemoteInput.IsKnownKey).ToList()); break;
                case "text":
                    string text = m.GetProperty("s").GetString() ?? "";
                    if (text.Length <= RemoteClipboard.MaxChars) input.Text(text);
                    break;
                case "release": input.ReleaseAll(); break;
                case "clip":
                    string clip = m.GetProperty("s").GetString() ?? "";
                    if (clip.Length <= RemoteClipboard.MaxChars) RemoteClipboard.SetText(clip);
                    break;
            }
        }

        static double X(JsonElement m) => m.GetProperty("x").GetDouble();
        static double Y(JsonElement m) => m.GetProperty("y").GetDouble();
        static int Int(JsonElement m, string name) => m.TryGetProperty(name, out var v) ? Math.Clamp(v.GetInt32(), -12000, 12000) : 0;

        // 目前看的螢幕 (遙控的座標以它為準)
        Rect Bounds() => Monitor().Bounds;

        Native.Monitor Monitor()
        {
            int s = screen;
            var list = monitors;
            return s >= 0 && s < list.Count ? list[s] : list.FirstOrDefault(x => x.Primary) ?? list[0];
        }

        // ===== 畫面靜止後補細節 =====
        // 為了延遲低，畫面一變只送一張、而且大小有限制，那一張是糊的 (2560x1440 的桌面實測 PSNR 27 dB)；
        // 以前畫面不再變就不送了，所以會一直停在糊的那張 (靜止時字糊的主要原因)。
        // 現在畫面變完之後繼續把同一張送給編碼器，讓它把細節補上去 (實測 1 秒內到 36 dB 以上)；
        // 補到編碼器沒東西可補 (連續 3 張都很小) 或超過 RefineMs 就停
        const int RefineMs = 3000, RefineDoneBytes = 1500;
        long refineUntil; // 補到什麼時候 (Environment.TickCount64)
        long changedAt;   // 畫面最近一次真的有變的時間
        long refineDone;  // 哪一次變化 (changedAt 的值) 已經補完了

        // ===== 畫面 =====
        // 截圖跟編碼分兩條執行緒：截下一張的同時編碼上一張 (單條執行緒 2560x1440 只有約 40 張/秒，分開後才到得了 60)。
        // 編碼跟不上時只編最新的一張，舊的直接丟掉，不會越積越慢
        Mat pending;
        readonly AutoResetEvent frameReady = new(false);

        void SendFrames()
        {
            Native.UsePerMonitorDpi();
            ScreenCapture capture = null;
            Native.Monitor captured = null; // 現在截的是哪個螢幕 (比實際的螢幕，不比編號：觀看頁一開始選的「螢幕 1」常常就是預設的主螢幕，不用重開)
            int lastQuality = -1;
            var clock = Stopwatch.StartNew();
            long lastSent = -10000, nextMonitorCheck = 2000;
            uint jpegFrame = 0;
            if (video) new Thread(EncodeFrames) { IsBackground = true, Name = "RemoteEncode-" + Id }.Start();
            try
            {
                while (closed == 0 && main.readyState == RTCDataChannelState.open)
                {
                    Quality q = CurrentQuality(quality);
                    // 每 2 秒看一次螢幕有沒有變 (拔掉 / 接上 / 省電斷線)：變了下面就會換成新的螢幕重新截
                    if (clock.ElapsedMilliseconds >= nextMonitorCheck)
                    {
                        nextMonitorCheck = clock.ElapsedMilliseconds + 2000;
                        RefreshMonitors();
                    }
                    int frameMs = video ? 1000 / q.Fps : q.JpegMs;
                    long wait = lastSent + frameMs - clock.ElapsedMilliseconds;
                    Native.PreciseSleep((int)wait);

                    var mon = Monitor();
                    if (mon != captured)
                    {
                        capture?.Dispose();
                        capture = new ScreenCapture(mon);
                        captured = mon;
                        forceFrame = true;
                    }
                    // 畫面沒變就不送；但每秒至少送一張 (掉封包後補回來、剛換畫質)，剛變完的那一陣子也繼續送 (補細節，見 refineUntil)
                    long g0 = clock.ElapsedMilliseconds;
                    bool refining = video && Environment.TickCount64 < Volatile.Read(ref refineUntil) && Volatile.Read(ref refineDone) != Volatile.Read(ref changedAt);
                    bool force = forceFrame || g0 - lastSent >= 1000 || lastQuality != quality || refining;
                    Mat img = capture.Grab(force ? (refining ? 8 : 50) : 200, force);
                    if (capture.Changed)
                    {
                        long t = Environment.TickCount64;
                        Volatile.Write(ref changedAt, t); // 要在交給編碼執行緒之前寫
                        Volatile.Write(ref refineUntil, t + RefineMs);
                    }
                    if ((capture.Problem ?? baseNote) != note)
                    {
                        note = capture.Problem ?? baseNote;
                        SendJson(w => { w.WriteString("t", "note"); if (note == null) w.WriteNull("s"); else w.WriteString("s", note); });
                    }
                    if (img == null) continue;
                    forceFrame = false;
                    lastQuality = quality;
                    lastSent = g0; // 從開始截圖算 (截圖要等下一次畫面更新，從截完算會每張多等 10 ms 左右)

                    if (video)
                    {
                        FramePool.Return(Interlocked.Exchange(ref pending, img)); // 交給編碼執行緒 (上一張還沒被拿走就不編了)
                        frameReady.Set();
                        continue;
                    }
                    using (img)
                    {
                        (int w, int h) = VideoSize(img.Width, img.Height, MaxWidth(q, img.Width));
                        using Mat small = img.Width == w && img.Height == h ? img.Clone() : img.Resize(new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
                        // 還沒送完的太多 (網路慢) 就跳過這張
                        if (main.bufferedAmount < 2 * 1024 * 1024) SendJpeg(small, q.JpegQuality, jpegFrame++);
                    }
                }
            }
            catch (Exception ex) { Logger.Warn($"[遠端畫面] 觀看端 {Id} 傳送中斷: {ex.Message}"); }
            finally
            {
                capture?.Dispose();
                FramePool.Clear();
            }
            Logger.Info($"[遠端畫面] 觀看端 {Id} 停止傳送");
            Close("停止傳送");
            frameReady.Set();
        }

        // 建立編碼器，告訴觀看頁實際用的是哪個 (選硬體但這台不能用時會是軟體)
        H264Encoder NewEncoder(int w, int h, Quality q, bool hardware)
        {
            var encoder = H264Encoder.Create(w, h, q.Fps, q.Bitrate, hardware, Video.HighProfile);
            Logger.Info($"[遠端畫面] 觀看端 {Id} 用 {encoder.Name} 編碼 ({w}x{h}，{(encoder.Profile == 100 ? "High" : "Baseline")} profile)");
            SendJson(wr => { wr.WriteString("t", "encoder"); wr.WriteBoolean("hw", encoder.Hardware); wr.WriteString("name", encoder.Name); });
            return encoder;
        }

        void EncodeFrames()
        {
            H264Encoder encoder = null;
            int encodedQuality = -1;
            bool encodedHardware = true; // 建立目前編碼器時觀看頁選的 (硬體建不起來時實際上是軟體，但不要每張都重試)
            var clock = Stopwatch.StartNew();
            long lastStamp = 0, smallChange = 0;
            int smallFrames = 0;
            try
            {
                while (closed == 0)
                {
                    if (!frameReady.WaitOne(500)) continue;
                    Mat img = Interlocked.Exchange(ref pending, null);
                    if (img == null) continue;
                    long change = Volatile.Read(ref changedAt); // 這張是哪一次變化之後的
                    using var lease = FramePool.ReturnLater(img); // 用完還回去給下一張用，不重新配置記憶體
                    int qi = quality;
                    Quality q = CurrentQuality(qi);
                    (int w, int h) = VideoSize(img.Width, img.Height, MaxWidth(q, img.Width));
                    Mat small = null;
                    if (img.Width != w || img.Height != h)
                    {
                        small = FramePool.Rent(h, w, img.Type());
                        Cv2.Resize(img, small, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
                    }
                    using var smallLease = FramePool.ReturnLater(small);
                    bool hw = wantHardware;
                    bool rebuild = encoder == null || encoder.Width != w || encoder.Height != h || encoder.Fps != q.Fps || encodedQuality != qi || encodedHardware != hw;
                    if (!rebuild && encoder.Bitrate != q.Bitrate) rebuild = !encoder.SetBitrate(q.Bitrate); // 自動畫質改碼率：編碼器不能途中改就重建
                    if (rebuild)
                    {
                        encoder?.Dispose();
                        encoder = null;
                        encoder = NewEncoder(w, h, q, hw);
                        encodedQuality = qi;
                        encodedHardware = hw;
                        Video.RequestKeyFrame();
                    }
                    long now = clock.ElapsedMilliseconds, duration = Math.Max(1, now - lastStamp);
                    bool key = Video.TakeKeyFrameRequest();
                    byte[] au;
                    try { au = encoder.Encode(small ?? img, key, duration * 10000); }
                    catch (Exception ex) when (encoder.Hardware)
                    {
                        // 硬體編碼器途中出錯 (驅動程式問題、顯示卡被別的程式占滿)：這次執行改用軟體編碼器，同一張重新編
                        Logger.Warn($"[遠端畫面] 觀看端 {Id} 硬體編碼器 {encoder.Name} 失敗，改用軟體編碼器: {ex.Message}");
                        H264Encoder.DisableHardware();
                        encoder.Dispose();
                        encoder = null;
                        encoder = NewEncoder(w, h, q, false);
                        au = encoder.Encode(small ?? img, true, duration * 10000);
                    }
                    if (au != null) Video.Send(au, (uint)(clock.ElapsedTicks * 90000 / Stopwatch.Frequency));
                    // 補細節補完了沒：同一次變化之後連續 3 張都很小 = 編碼器沒東西可補了
                    if (au != null)
                    {
                        if (change != smallChange || au.Length >= RefineDoneBytes) { smallChange = change; smallFrames = 0; }
                        if (au.Length < RefineDoneBytes && ++smallFrames >= 3) Volatile.Write(ref refineDone, change);
                    }
                    lastStamp = now;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[遠端畫面] 觀看端 {Id} 編碼失敗: {ex.Message}");
                Close("編碼失敗");
            }
            finally
            {
                encoder?.Dispose();
                Interlocked.Exchange(ref pending, null)?.Dispose();
                FramePool.Clear();
            }
        }

        /// <summary>送出的影像大小：寬度不超過 maxWidth，寬高都是偶數 (H.264 要求)</summary>
        public static (int W, int H) VideoSize(int width, int height, int maxWidth)
        {
            int w = Math.Min(width, maxWidth);
            int h = (int)Math.Round((double)height * w / width);
            return (w & ~1, h & ~1);
        }

        // JPEG 切塊：標頭 = 畫面編號 u32、第幾塊 u16、共幾塊 u16 (little endian) + 片段
        const int ChunkBytes = 16000;

        void SendJpeg(Mat bgra, int jpegQuality, uint frame)
        {
            using var bgr = bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
            Cv2.ImEncode(".jpg", bgr, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality));
            ushort count = (ushort)((jpeg.Length + ChunkBytes - 1) / ChunkBytes);
            for (ushort i = 0; i < count; i++)
            {
                int len = Math.Min(ChunkBytes, jpeg.Length - i * ChunkBytes);
                var pkt = new byte[8 + len];
                BitConverter.TryWriteBytes(pkt.AsSpan(0), frame);
                BitConverter.TryWriteBytes(pkt.AsSpan(4), i);
                BitConverter.TryWriteBytes(pkt.AsSpan(6), count);
                Buffer.BlockCopy(jpeg, i * ChunkBytes, pkt, 8, len);
                main.send(pkt);
            }
        }

        void SendJson(Action<Utf8JsonWriter> body)
        {
            try { if (main?.readyState == RTCDataChannelState.open) main.send(RemoteView.Json(body)); }
            catch (Exception ex) { Logger.Info($"[遠端畫面] 觀看端 {Id} 訊息送不出去: {ex.Message}"); }
        }

        /// <summary>
        /// 觀看端就是這台電腦 (在本機開觀看頁) 就不給遙控：滑鼠移到觀看頁的畫面上 → 游標被移到對應位置 → 還在觀看頁的畫面裡 → 又送一次，
        /// 游標會被自己拉住、連遙控按鈕都按不到 (使用者回報)。對方位址是這台的網路卡位址就當作同一台
        /// </summary>
        /// <summary>執行中被關掉遙控：放開還按著的鍵，之後的遙控訊息都不理</summary>
        public void DisableControl()
        {
            if (!Control) return;
            Control = false;
            input?.ReleaseAll();
            RemoteLog.Write(Id, "電腦端關閉了遙控");
            SendCaps();
        }

        void CheckSameComputer()
        {
            if (!Control || sameComputerChecked || dryRun) return; // dry 不會真的送輸入，不會拉住游標 (測試用)
            var ep = pc.GetRtpChannel()?.NominatedEntry?.RemoteCandidate?.DestinationEndPoint;
            if (ep == null) return;
            sameComputerChecked = true;
            var addr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            bool local = System.Net.IPAddress.IsLoopback(addr) || System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(u => u.Address.Equals(addr));
            if (!local) return;
            Control = false;
            Logger.Info($"[遠端畫面] 觀看端 {Id} 就在這台電腦上 ({addr})，不給遙控");
            RemoteLog.Write(Id, $"觀看端就在這台電腦上 ({addr})，改成只能看");
            baseNote = SameComputerText; // 畫面迴圈會送給觀看頁
        }

        bool sameComputerChecked;
        string baseNote; // 一直顯示的提示 (截圖沒問題時)
        const string SameComputerText = "觀看頁開在這台電腦上，遙控會讓滑鼠被畫面拉住，所以只能看；請用手機或別台電腦連線";

        // 實際連上的對方位址 (ICE 選中的那一組)
        string Source()
        {
            try
            {
                var c = pc.GetRtpChannel()?.NominatedEntry?.RemoteCandidate;
                if (c == null) return "不明";
                string ep = c.DestinationEndPoint?.ToString() ?? $"{c.address}:{c.port}";
                string kind = c.type switch
                {
                    RTCIceCandidateType.host => "區網",
                    RTCIceCandidateType.srflx or RTCIceCandidateType.prflx => "外網",
                    RTCIceCandidateType.relay => "中繼",
                    _ => c.type.ToString(),
                };
                return $"{ep} ({kind})";
            }
            catch { return "不明"; }
        }

        /// <summary>中斷連線；bye = 先告訴觀看端原因 (觀看頁就不會自動重連)</summary>
        public void Close(string reason, string bye = null)
        {
            if (Interlocked.Exchange(ref closed, 1) == 1) return;
            if (bye != null)
            {
                SendJson(w => { w.WriteString("t", "bye"); w.WriteString("s", bye); });
                Thread.Sleep(200); // 等訊息送出去再關
            }
            if (Control) RemoteClipboard.Unwatch(onClipboard);
            input?.Dispose();
            try { pc.close(); } catch { }
            string used = input?.Used == true ? "，有遙控操作" : "";
            if (connectedAt != null)
                RemoteLog.Write(Id, $"中斷連線 ({reason})  連了 {RemoteLog.Duration(DateTime.Now - connectedAt.Value)}{used}");
            else
            {
                string hint = "";
                if (offeredCandidates != null)
                {
                    Logger.Info($"[遠端畫面] 觀看端 {Id} 沒有連上：觀看端給的位址 {offeredCandidates}；收到觀看端的連線測試 {checksIn} 次" +
                                (candidateError != null ? $"；{candidateError}" : ""));
                    if (checksIn == 0) hint = "，觀看端的封包都沒進來 (可能被 Windows 防火牆擋住，或雙方網路不允許直連)";
                }
                RemoteLog.Write(Id, $"沒有連上 ({reason}){hint}");
            }
            RemoteView.Remove(this);
        }

        // ===== 沒連上時的診斷 (寫日誌)：觀看端給了哪些位址、觀看端的連線測試封包有沒有進來 =====
        string offeredCandidates, candidateError;
        int checksIn;

        /// <summary>設好觀看端的連線碼之後呼叫 (那時才有 ICE 通道)</summary>
        public void WatchIce(string offerSdp)
        {
            offeredCandidates = DescribeCandidates(offerSdp);
            var ice = pc.GetRtpChannel();
            if (ice == null) return;
            ice.OnStunMessageReceived += (msg, ep, relayed) =>
            {
                if (msg.Header.MessageType == STUNMessageTypesEnum.BindingRequest) Interlocked.Increment(ref checksIn);
            };
            ice.OnIceCandidateError += (c, error) => Interlocked.CompareExchange(ref candidateError, error, null);
        }

        // 連線碼裡的位址：區網 (Chrome 會藏成 xxx.local，要 mDNS 查)、外網 (STUN 查到的)、中繼；只算 UDP
        static string DescribeCandidates(string sdp)
        {
            int hidden = 0, host = 0, srflx = 0, relay = 0;
            foreach (string raw in sdp.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("a=candidate:")) continue;
                string[] f = line.Split(' ');
                if (f.Length < 8 || f[6] != "typ" || !f[2].Equals("udp", StringComparison.OrdinalIgnoreCase)) continue;
                switch (f[7])
                {
                    case "host": if (f[4].EndsWith(".local", StringComparison.OrdinalIgnoreCase)) hidden++; else host++; break;
                    case "srflx": case "prflx": srflx++; break;
                    case "relay": relay++; break;
                }
            }
            var parts = new List<string>();
            if (hidden > 0) parts.Add($"區網 {hidden} 個 (藏成 .local)");
            if (host > 0) parts.Add($"區網 {host} 個");
            if (srflx > 0) parts.Add($"外網 {srflx} 個");
            if (relay > 0) parts.Add($"中繼 {relay} 個");
            return parts.Count > 0 ? string.Join("、", parts) : "沒有";
        }
    }
}
