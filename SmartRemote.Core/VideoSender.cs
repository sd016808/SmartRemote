
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SmartRemote.Core
{
    /// <summary>
    /// 一個觀看端的 H.264 影像軌 (WebRTC)：協商 (挑 profile、加 playout-delay 擴充)、切成 RTP 封包送出、掉封包重送、
    /// 瀏覽器要求關鍵畫格。SIPSorcery 原本的做法有好幾處不能用 (時間戳記、只看得到最後一種回饋、不認得 playout-delay)，
    /// 這裡自己做；每一處的原因寫在那一段。RemoteSession 只用到：Negotiated / HighProfile、Send、TakeKeyFrameRequest
    /// </summary>
    sealed class VideoSender
    {
        readonly RTCPeerConnection pc;
        readonly string id;
        readonly Func<int> bitrate; // 目前的碼率 (bps)：限制重送的量用
        volatile int keyFrameWanted = 1;

        /// <summary>影像軌協商成功 (觀看端有 H.264、這台能編碼)；false = 用 JPEG (舊版觀看頁 / Windows N 版)</summary>
        public bool Negotiated { get; private set; }
        /// <summary>協商到 High profile (編碼器也要用 High)</summary>
        public bool HighProfile { get; private set; }

        public VideoSender(RTCPeerConnection pc, string id, Func<int> bitrate)
        {
            this.pc = pc;
            this.id = id;
            this.bitrate = bitrate;
            // 瀏覽器掉封包解不出畫面時會要求關鍵畫格 (PLI / FIR)。
            // 只認這兩種：PSFB 還有 REMB (瀏覽器每 0.2 秒回報一次頻寬)，當成要求的話每秒會白送 5 張關鍵畫格。
            // 這裡只是備援：SIPSorcery 一個封包只留最後一種回饋，而 Chrome 每個封包最後都帶 REMB，PLI 幾乎都被蓋掉，
            // 真正在處理的是 WatchFeedback (自己解讀)
            pc.OnReceiveReport += (_, media, report) =>
            {
                if (media == SDPMediaTypesEnum.video && report.Feedback?.Header is { PacketType: RTCPReportTypesEnum.PSFB, PayloadFeedbackMessageType: PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR })
                    WantKeyFrame();
            };
        }

        /// <summary>這一張要不要編成關鍵畫格 (問過就清掉)</summary>
        public bool TakeKeyFrameRequest() => Interlocked.Exchange(ref keyFrameWanted, 0) == 1;

        /// <summary>下一張一定要關鍵畫格 (例如換了編碼器)</summary>
        public void RequestKeyFrame() => keyFrameWanted = 1;

        // ===== 協商 =====
        // 這台能不能用 H.264 編碼器 (Windows N 版沒有)：第一次用到時試建一個
        static readonly Lazy<bool> H264Available = new(() =>
        {
            try { using var e = new H264Encoder(320, 240, 30, 500_000); return true; }
            catch (Exception ex) { Logger.Warn($"[遠端畫面] 不能用 H.264 編碼器，改用 JPEG: {ex.Message}"); return false; }
        });

        /// <summary>
        /// 觀看端的連線碼有影像軌而且有 H.264 (packetization-mode=1) 就加上送出的影像軌；要在 setRemoteDescription 之前呼叫。
        /// 優先 High profile (64xxxx：Chrome、Edge、Safari 都有提供)，同樣流量畫質比較好；沒有才用 Constrained Baseline (42e01f)
        /// </summary>
        public void AddTrack(string offerSdp)
        {
            if (!H264Available.Value) return;
            var rtpmap = new Dictionary<int, string>();
            var fmtp = new Dictionary<int, string>();
            foreach (string raw in offerSdp.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("a=rtpmap:") && line.Contains(" H264/90000")) rtpmap[int.Parse(line[9..line.IndexOf(' ')])] = line;
                else if (line.StartsWith("a=fmtp:") && int.TryParse(line[7..line.IndexOf(' ')], out int pt)) fmtp[pt] = line[(line.IndexOf(' ') + 1)..];
                else if (line.StartsWith("a=extmap:") && line.EndsWith(" " + PlayoutDelayUri) && int.TryParse(line[9..line.IndexOf(' ')], out int id)) playoutDelayId = id;
            }
            static int Rank(string f) => f.Contains("profile-level-id=640c") || f.Contains("profile-level-id=6400") ? 0 : f.Contains("profile-level-id=42e0") ? 1 : f.Contains("profile-level-id=42") ? 2 : 9;
            var candidates = rtpmap.Keys.Where(pt => fmtp.TryGetValue(pt, out string f) && f.Contains("packetization-mode=1") && Rank(f) < 9).ToList();
            if (candidates.Count == 0) return;
            int best = candidates.OrderBy(pt => Rank(fmtp[pt])).First();
            HighProfile = Rank(fmtp[best]) == 0;
            pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, best, 90000, fmtp[best]), MediaStreamStatusEnum.SendOnly));
            Negotiated = true;
            payloadType = best;
        }

        /// <summary>回覆建立好之後確認影像軌有沒有真的協商成功 (回覆裡影像是 sendonly)</summary>
        public void CheckAnswer(string answerSdp)
        {
            if (!Negotiated) return;
            int m = answerSdp.IndexOf("m=video", StringComparison.Ordinal);
            int next = m < 0 ? -1 : answerSdp.IndexOf("\nm=", m, StringComparison.Ordinal);
            string section = m < 0 ? "" : next < 0 ? answerSdp[m..] : answerSdp[m..next];
            Negotiated = section.Contains("a=sendonly") && !section.StartsWith("m=video 0 ");
        }

        // playout-delay 擴充 (min = max = 0)：告訴瀏覽器「解完碼馬上顯示」。
        // 沒有的話 Chrome 會照畫格的節奏排顯示時間 (平滑播放)：連續在動時多等 20 ms 左右，
        // 畫面偶爾才變一下時 (打字、點一下) 常常排錯，壓住最多一整個間隔 (實測到 700 ms) 才顯示
        const string PlayoutDelayUri = "http://www.webrtc.org/experiments/rtp-hdrext/playout-delay";
        int playoutDelayId; // 觀看端的連線碼裡這個擴充的編號 (0 = 瀏覽器不支援)

        sealed class PlayoutDelayExtension : RTPHeaderExtension
        {
            public PlayoutDelayExtension(int id) : base(id, PlayoutDelayUri, 3, RTPHeaderExtensionType.OneByte, SDPMediaTypesEnum.video) { }
            public override void Set(object obj) { }
            public override byte[] Marshal() => new byte[] { (byte)((Id << 4) | 2), 0, 0, 0 }; // 編號 + 長度，min 12 位元、max 12 位元都是 0
            public override object Unmarshal(RTPHeader header, byte[] data) => null;
        }

        /// <summary>
        /// 回覆的連線碼加上 playout-delay 擴充，之後送出的影像封包都帶著它；要在 CheckAnswer 之後呼叫。
        /// SIPSorcery 不認得這個擴充、不會自己寫進回覆，所以直接改回覆的文字 (觀看端有提出才加，編號用它的)
        /// </summary>
        public string AddPlayoutDelay(string answerSdp)
        {
            if (!Negotiated || playoutDelayId < 1 || playoutDelayId > 14) return answerSdp; // 1 ~ 14 才能用單位元組的擴充標頭
            int m = answerSdp.IndexOf("m=video", StringComparison.Ordinal);
            int a = m < 0 ? -1 : answerSdp.IndexOf("\na=", m, StringComparison.Ordinal);
            int end = a < 0 ? -1 : answerSdp.IndexOf('\n', a + 1);
            if (end < 0) return answerSdp;
            pc.VideoStream.LocalTrack.HeaderExtensions[playoutDelayId] = new PlayoutDelayExtension(playoutDelayId);
            return answerSdp.Insert(end + 1, $"a=extmap:{playoutDelayId} {PlayoutDelayUri}\r\n");
        }

        // ===== 送出 =====
        // 一張 H.264 畫面 (Annex B) 切成 RTP 封包送出，時間戳記 (90 kHz) 由呼叫端給 = 送出的時間。
        // 不用 pc.SendVideo：它的戳記是「送完這張才加上這張的長度」，等於每張都蓋上一張的時間。畫面一直在動時沒差；
        // 靜止 (每秒 1 張) 後開始動，戳記會比實際早將近 1 秒，瀏覽器以為畫面來早了就壓著晚點才顯示 (實測多 100 ms 左右)
        int payloadType;
        const int RtpPayloadBytes = 1200;
        const string TransportCcUri = "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01";

        public void Send(byte[] au, uint timestamp)
        {
            int count = 0;
            foreach (var item in H264Packetiser.ParseNals(au))
            {
                byte[] nal = item.NAL;
                if (nal.Length <= RtpPayloadBytes)
                {
                    SendPacket(nal, timestamp, item.IsLast ? 1 : 0, ref count);
                    continue;
                }
                // 一個封包放不下：切成 FU-A (第一個位元組是 NAL 標頭，不送，改放在每塊的 FU 標頭裡)
                for (int offset = 1; offset < nal.Length; offset += RtpPayloadBytes)
                {
                    int len = Math.Min(RtpPayloadBytes, nal.Length - offset);
                    bool last = offset + len >= nal.Length;
                    byte[] header = H264Packetiser.GetH264RtpHeader(nal[0], offset == 1, last);
                    var packet = new byte[header.Length + len];
                    Buffer.BlockCopy(header, 0, packet, 0, header.Length);
                    Buffer.BlockCopy(nal, offset, packet, header.Length, len);
                    SendPacket(packet, timestamp, item.IsLast && last ? 1 : 0, ref count);
                }
            }
        }

        // ===== 掉封包 =====
        // 最近送出的影像封包留著，瀏覽器說沒收到 (NACK) 就重送同一個；瀏覽器要求關鍵畫格 (PLI / FIR) 就送。
        // 沒有重送的話掉任何一個封包那張就解不開、後面的也跟著解不開，只能等關鍵畫格；而關鍵畫格有兩三百個封包，
        // 掉 1% 就幾乎不可能整張到齊，畫面會一直卡住 (實測掉 1% 封包時一張都解不出來)
        const int SentSize = 2048; // 留最近幾個封包 (2 的次方)；20 Mbps 大約 1 秒
        const int BurstPackets = 48; // 一口氣最多送幾個封包 (約 58 KB)，超過就停 1 ms 再送
        readonly (byte[] Payload, uint Timestamp, int Marker, int Seq, long ResentAt)[] sent = new (byte[], uint, int, int, long)[SentSize];
        readonly object sendLock = new(); // 編碼執行緒在送、收到 NACK 的執行緒要重送
        int lastSeq = -1;
        long keyFrameAt;
        long resendBudget, resendBudgetAt;

        void SendPacket(byte[] payload, uint timestamp, int marker, ref int count)
        {
            // 一張畫面的封包全部一口氣送出去的話，大的畫面 (關鍵畫格兩三百個封包) 會塞爆對方的接收緩衝區或無線網路，中間掉一段；分批送
            if (++count % BurstPackets == 0) Native.PreciseSleep(1);
            var stream = pc.VideoStream;
            lock (sendLock)
            {
                ushort seq = stream.LocalTrack.GetNextSeqNum();
                sent[seq & (SentSize - 1)] = (payload, timestamp, marker, seq, 0);
                lastSeq = seq;
                stream.SetRtpHeaderExtensionValue(TransportCcUri, null); // 有協商到才會帶 (封包序號加一)，跟 SendVideo 一樣
                stream.SendRtpRaw(payload, timestamp, marker, payloadType, seq);
            }
        }

        // 回傳 false = 這個封包沒辦法重送 (呼叫端改送關鍵畫格)
        bool Resend(ushort seq)
        {
            lock (sendLock)
            {
                ref var p = ref sent[seq & (SentSize - 1)];
                // 序號繞一圈 (65535 → 0) 之前的不重送：SIPSorcery 加密用的圈數是照「送出 65535 就加一」算的，
                // 重送上一圈的會用錯圈數 (對方解不開)，重送 65535 本身還會讓圈數多加一次、之後全部解不開
                if (p.Payload == null || p.Seq != seq || seq > lastSeq || seq == ushort.MaxValue) return false;
                long now = Environment.TickCount64;
                if (now - p.ResentAt < 10) return true; // 剛重送過 (同一個要求重複收到)

                // 重送的量有上限：流量的 1/4，最多存 0.5 秒的份。網路塞住時會一次掉一大堆，全部重送只會更塞、永遠清不掉 (實測會卡好幾秒)；
                // 超過就不重送了，改成送一張關鍵畫格重新開始
                int rate = bitrate() / 8 / 4; // 每秒可以重送幾 bytes
                resendBudget = Math.Min(rate / 2, resendBudget + (now - resendBudgetAt) * rate / 1000);
                resendBudgetAt = now;
                if (resendBudget < p.Payload.Length) { WantKeyFrame(1000); return true; }
                resendBudget -= p.Payload.Length;

                p.ResentAt = now;
                pc.VideoStream.SendRtpRaw(p.Payload, p.Timestamp, p.Marker, payloadType, seq);
                return true;
            }
        }

        // 關鍵畫格很大，要求太密的話只會塞得更嚴重：最少隔 0.3 秒 (重送不完改送關鍵畫格的隔 1 秒)
        void WantKeyFrame(int minGapMs = 300)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref keyFrameAt) < minGapMs) return;
            Interlocked.Exchange(ref keyFrameAt, now);
            keyFrameWanted = 1;
        }

        // ===== 瀏覽器的回饋 (RTCP) =====
        bool feedbackWatched;

        /// <summary>
        /// 連上之後呼叫：自己解讀瀏覽器送來的回饋 (RTCP)。SIPSorcery 解完只留每個封包裡最後一種回饋、NACK 也只讀第一筆，沒辦法用；
        /// 所以把 SRTCP 的解密函式包一層，解密成功後順便看內容 (SetSecurityContext 是公開的方法，其他三個函式照舊)。
        /// 收到的 RTCP 用哪一條軌的解密函式要看對方的 SSRC 對到誰，對不到就用第一條 (沒有聲音時也有一條空的聲音軌)，所以每一條都包
        /// </summary>
        public void WatchFeedback()
        {
            if (!Negotiated || feedbackWatched) return;
            var streams = pc.AudioStreamList.Cast<MediaStream>().Concat(pc.VideoStreamList).Concat(pc.TextStreamList).Where(x => x?.GetSecurityContext() != null).ToList();
            if (streams.Count == 0) return;
            feedbackWatched = true;
            foreach (MediaStream stream in streams)
            {
                var context = stream.GetSecurityContext();
                ProtectRtpPacket unprotect = context.UnprotectRtcpPacket;
                stream.SetSecurityContext(context.ProtectRtpPacket, context.UnprotectRtpPacket, context.ProtectRtcpPacket, (byte[] buffer, int length, out int outLength) =>
                {
                    int result = unprotect(buffer, length, out outLength);
                    if (result == 0)
                    {
                        try { OnFeedback(buffer, outLength); }
                        catch (Exception ex) { Logger.Warn($"[遠端畫面] 觀看端 {id} 的回饋解讀失敗: {ex.Message}"); }
                    }
                    return result;
                });
            }
        }

        // 一個 RTCP 封包裡有好幾段，每段：版本 / 種類 (1 位元組)、型別 (1)、長度 (2，單位 4 位元組再加一段)
        void OnFeedback(byte[] b, int length)
        {
            for (int i = 0; i + 4 <= length;)
            {
                int format = b[i] & 0x1F, type = b[i + 1], size = 4 * (((b[i + 2] << 8) | b[i + 3]) + 1);
                if (b[i] >> 6 != 2 || i + size > length) return;
                if (type == 206 && (format == 1 || format == 4)) WantKeyFrame(); // PSFB：PLI / FIR
                else if (type == 205 && format == 1) // RTPFB：NACK，每筆 = 掉的序號 + 後面 16 個哪些也掉了
                {
                    bool all = true;
                    for (int j = i + 12; j + 4 <= i + size; j += 4)
                    {
                        int first = (b[j] << 8) | b[j + 1], mask = (b[j + 2] << 8) | b[j + 3];
                        all &= Resend((ushort)first);
                        for (int k = 0; k < 16; k++)
                            if ((mask >> k & 1) != 0) all &= Resend((ushort)(first + k + 1));
                    }
                    // 有重送不了的 (太舊、序號剛繞一圈)：瀏覽器會一直等到 3 秒逾時才自己要求關鍵畫格，畫面就停 3 秒；直接送一張
                    if (!all) WantKeyFrame();
                }
                i += size;
            }
        }
    }
}
