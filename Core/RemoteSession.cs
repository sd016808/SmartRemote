
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

        public string Id { get; }
        public bool Control { get; private set; }
        public bool Connected => connectedAt != null;

        readonly RTCPeerConnection pc;
        readonly bool newViewer, dryRun;
        bool video;              // 影像軌協商成功
        RTCDataChannel main;
        RemoteInput input;
        List<Native.Monitor> monitors = Native.Monitors();
        volatile int screen = -1, quality = 1, keyFrameWanted = 1, closed;
        volatile bool forceFrame = true;
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

            pc.onconnectionstatechange += s =>
            {
                Logger.Info($"[遠端畫面] 觀看端 {id}：{s}");
                if (s == RTCPeerConnectionState.connected && connectedAt == null)
                {
                    connectedAt = DateTime.Now; // 顯示用時間
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
            // 瀏覽器掉封包解不出畫面時會要求關鍵畫格 (PLI / FIR)
            pc.OnReceiveReport += (_, media, report) =>
            {
                if (media == SDPMediaTypesEnum.video && report.Feedback?.Header.PacketType == RTCPReportTypesEnum.PSFB) keyFrameWanted = 1;
            };
        }

        /// <summary>
        /// 觀看端的連線碼有影像軌而且有 H.264 (packetization-mode=1) 就加上送出的影像軌；要在 setRemoteDescription 之前呼叫。
        /// 優先 Constrained Baseline (42e01f)，瀏覽器都能硬體 / 軟體解碼
        /// </summary>
        public void AddVideoTrack(string offerSdp)
        {
            if (!H264Available.Value) return;
            var rtpmap = new Dictionary<int, string>();
            var fmtp = new Dictionary<int, string>();
            foreach (string raw in offerSdp.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("a=rtpmap:") && line.Contains(" H264/90000")) rtpmap[int.Parse(line[9..line.IndexOf(' ')])] = line;
                else if (line.StartsWith("a=fmtp:") && int.TryParse(line[7..line.IndexOf(' ')], out int pt)) fmtp[pt] = line[(line.IndexOf(' ') + 1)..];
            }
            var candidates = rtpmap.Keys.Where(pt => fmtp.TryGetValue(pt, out string f) && f.Contains("packetization-mode=1") && f.Contains("profile-level-id=42")).ToList();
            if (candidates.Count == 0) return;
            int best = candidates.OrderBy(pt => fmtp[pt].Contains("profile-level-id=42e0") ? 0 : 1).First();
            pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, best, 90000, fmtp[best]), MediaStreamStatusEnum.SendOnly));
            video = true;
        }

        /// <summary>回覆建立好之後確認影像軌有沒有真的協商成功 (回覆裡影像是 sendonly)</summary>
        public void CheckAnswer(string answerSdp)
        {
            if (!video) return;
            int m = answerSdp.IndexOf("m=video", StringComparison.Ordinal);
            int next = m < 0 ? -1 : answerSdp.IndexOf("\nm=", m, StringComparison.Ordinal);
            string section = m < 0 ? "" : next < 0 ? answerSdp[m..] : answerSdp[m..next];
            video = section.Contains("a=sendonly") && !section.StartsWith("m=video 0 ");
        }

        // 這台能不能用 H.264 編碼器 (Windows N 版沒有)：第一次用到時試建一個
        static readonly Lazy<bool> H264Available = new(() =>
        {
            try { using var e = new H264Encoder(320, 240, 30, 500_000); return true; }
            catch (Exception ex) { Logger.Warn($"[遠端畫面] 不能用 H.264 編碼器，改用 JPEG: {ex.Message}"); return false; }
        });

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
                // 能力 (新版觀看頁看這個決定要顯示哪些功能；舊版會忽略)
                SendJson(w =>
                {
                    w.WriteString("t", "caps");
                    w.WriteNumber("v", 2);
                    w.WriteBoolean("video", video);
                    w.WriteBoolean("control", Control);
                });
                // 螢幕清單：{"t":"screens","list":[{"i":0,"w":2560,"h":1440,"primary":true}, ...]}
                SendJson(w =>
                {
                    w.WriteString("t", "screens");
                    w.WriteStartArray("list");
                    for (int i = 0; i < monitors.Count; i++)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("i", i);
                        w.WriteNumber("w", monitors[i].Bounds.Width);
                        w.WriteNumber("h", monitors[i].Bounds.Height);
                        w.WriteBoolean("primary", monitors[i].Primary);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
                if (Control) RemoteClipboard.Watch(onClipboard);
                Logger.Info($"[遠端畫面] 觀看端 {Id} 開始傳送畫面 ({monitors.Count} 個螢幕，{(video ? "H.264 影像" : "JPEG")}，{(Control ? "可遙控" : "只能看")})");
                new Thread(SendFrames) { IsBackground = true, Name = "RemoteVideo-" + Id }.Start();
            }
            dc.onopen += Begin;
            if (dc.readyState == RTCDataChannelState.open) Begin();
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
                    quality = Math.Clamp(m.GetProperty("q").GetInt32(), 0, Qualities.Length - 1);
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
            return s >= 0 && s < monitors.Count ? monitors[s] : monitors.FirstOrDefault(x => x.Primary) ?? monitors[0];
        }

        // ===== 畫面 =====
        // 截圖跟編碼分兩條執行緒：截下一張的同時編碼上一張 (單條執行緒 2560x1440 只有約 40 張/秒，分開後才到得了 60)。
        // 編碼跟不上時只編最新的一張，舊的直接丟掉，不會越積越慢
        Mat pending;
        readonly AutoResetEvent frameReady = new(false);

        void SendFrames()
        {
            Native.UsePerMonitorDpi();
            ScreenCapture capture = null;
            int capturedScreen = -2, lastQuality = -1;
            var clock = Stopwatch.StartNew();
            long lastSent = -10000;
            uint jpegFrame = 0;
            if (video) new Thread(EncodeFrames) { IsBackground = true, Name = "RemoteEncode-" + Id }.Start();
            try
            {
                while (closed == 0 && main.readyState == RTCDataChannelState.open)
                {
                    Quality q = newViewer ? Qualities[quality] : OldViewer;
                    int frameMs = video ? 1000 / q.Fps : q.JpegMs;
                    long wait = lastSent + frameMs - clock.ElapsedMilliseconds;
                    Native.PreciseSleep((int)wait);

                    int s = screen;
                    if (s != capturedScreen)
                    {
                        capture?.Dispose();
                        capture = new ScreenCapture(Monitor());
                        capturedScreen = s;
                        forceFrame = true;
                    }
                    // 畫面沒變就不送；但每秒至少送一張 (掉封包後補回來、剛換畫質)
                    bool force = forceFrame || clock.ElapsedMilliseconds - lastSent >= 1000 || lastQuality != quality;
                    long g0 = clock.ElapsedMilliseconds;
                    Mat img = capture.Grab(force ? 50 : 200, force);
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
                        Interlocked.Exchange(ref pending, img)?.Dispose(); // 交給編碼執行緒
                        frameReady.Set();
                        continue;
                    }
                    using (img)
                    {
                        (int w, int h) = VideoSize(img.Width, img.Height, q.MaxWidth);
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
            }
            Logger.Info($"[遠端畫面] 觀看端 {Id} 停止傳送");
            Close("停止傳送");
            frameReady.Set();
        }

        void EncodeFrames()
        {
            H264Encoder encoder = null;
            int encodedQuality = -1;
            var clock = Stopwatch.StartNew();
            long lastStamp = 0;
            try
            {
                while (closed == 0)
                {
                    if (!frameReady.WaitOne(500)) continue;
                    using Mat img = Interlocked.Exchange(ref pending, null);
                    if (img == null) continue;
                    int qi = quality;
                    Quality q = Qualities[qi];
                    (int w, int h) = VideoSize(img.Width, img.Height, q.MaxWidth);
                    using Mat small = img.Width == w && img.Height == h ? null : img.Resize(new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
                    if (encoder == null || encoder.Width != w || encoder.Height != h || encodedQuality != qi)
                    {
                        encoder?.Dispose();
                        encoder = new H264Encoder(w, h, q.Fps, q.Bitrate);
                        encodedQuality = qi;
                        keyFrameWanted = 1;
                    }
                    long now = clock.ElapsedMilliseconds, duration = Math.Max(1, now - lastStamp);
                    bool key = Interlocked.Exchange(ref keyFrameWanted, 0) == 1;
                    byte[] au = encoder.Encode(small ?? img, key, duration * 10000);
                    if (au != null) pc.SendVideo((uint)(duration * 90), au);
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
            SendJson(w => { w.WriteString("t", "caps"); w.WriteNumber("v", 2); w.WriteBoolean("video", video); w.WriteBoolean("control", false); });
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
                RemoteLog.Write(Id, $"沒有連上 ({reason})");
            RemoteView.Remove(this);
        }
    }
}
