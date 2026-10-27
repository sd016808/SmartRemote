using OpenCvSharp;
using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>
    /// H.264 編碼 (Windows 內建 Media Foundation，不用另外帶 dll)：BGRA 畫面 → H.264 (Annex B，給 WebRTC 的影像軌)。
    /// 用 Create 建立：優先用顯示卡的硬體編碼器 (NVIDIA NVENC / Intel Quick Sync / AMD AMF，由顯示卡驅動程式提供)，
    /// 沒有或建立失敗就用 Windows 內建的軟體編碼器。
    /// 用 Constrained Baseline、低延遲模式 (一張進一張出，沒有 B 畫格)，瀏覽器都能解。
    /// Windows N 版沒有裝媒體功能套件時建立會失敗，呼叫端改用 JPEG
    /// </summary>
    sealed unsafe class H264Encoder : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        /// <summary>true = 顯示卡的硬體編碼器</summary>
        public bool Hardware { get; }
        public string Name { get; }
        public int Fps { get; }
        public int Bitrate { get; private set; }

        IntPtr activate, transform, codecApi, events, outSample, outBuffer;
        uint inStream, outStream;
        bool async, nv12, providesSamples;
        int needInput, inputs, outputs, lag; // 非同步 (硬體)：編碼器要了幾張、送了幾張、吐了幾份；lag = 這個編碼器固定晚幾張才吐
        readonly int frameSize;
        byte[] sequenceHeader; // SPS + PPS：每張關鍵畫格前面都要有 (中途加入 / 掉封包後才解得開)
        long frameTime;

        static readonly Guid CLSID_MSH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
        static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
        static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        static readonly Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
        static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
        static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
        static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_IYUV = new("56555949-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // 也是 CODECAPI_AVLowLatencyMode
        static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
        static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

        const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
        const uint METransformNeedInput = 601, METransformHaveOutput = 602;
        const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

        [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
        [DllImport("mfplat.dll")] static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, IntPtr** activates, uint* count);
        [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr obj);
        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

        [StructLayout(LayoutKind.Sequential)] struct TypeInfo { public Guid Major, Sub; }
        [StructLayout(LayoutKind.Sequential)] struct StreamInfo { public uint Flags, Size, Alignment; }
        [StructLayout(LayoutKind.Sequential)] struct OutputDataBuffer { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public uint UInt; [FieldOffset(8)] public short Bool; }

        static readonly Lazy<int> started = new(() => MFStartup(0x20070, 0)); // MF_VERSION，MFSTARTUP_FULL
        static volatile bool hardwareDisabled;

        static void Startup()
        {
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED；已經初始化過也沒關係
            Native.Check(started.Value, "MFStartup");
        }

        /// <summary>硬體編碼器編碼途中出錯：之後這次執行都只用軟體編碼器 (顯示卡驅動有問題時不要每次都再試)</summary>
        public static void DisableHardware() => hardwareDisabled = true;

        /// <summary>這台有沒有硬體 H.264 編碼器 (第一次用到時查一次)；編碼途中出錯停用後就當作沒有</summary>
        public static bool HasHardware => !hardwareDisabled && hardwareCount.Value > 0;

        static readonly Lazy<int> hardwareCount = new(() =>
        {
            try
            {
                IntPtr* list = EnumHardware(out uint count);
                for (int i = 0; i < count; i++) Native.Release(ref list[i]);
                if (list != null) CoTaskMemFree((IntPtr)list);
                return (int)count;
            }
            catch (Exception) { return 0; }
        });

        // 列出硬體 H.264 編碼器 (NV12 進、H.264 出)；用完每個都要 Release，陣列要 CoTaskMemFree
        static IntPtr* EnumHardware(out uint count)
        {
            Startup();
            var input = new TypeInfo { Major = MFMediaType_Video, Sub = MFVideoFormat_NV12 };
            var output = new TypeInfo { Major = MFMediaType_Video, Sub = MFVideoFormat_H264 };
            IntPtr* list = null;
            uint n = 0;
            if (MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 0x4 | 0x40, &input, &output, &list, &n) < 0) list = null; // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER
            count = list == null ? 0 : n;
            return list;
        }

        /// <summary>建立編碼器：hardware = true 先試顯示卡的硬體編碼器 (可能不只一個，例如內顯 + 獨顯，照系統排的順序試)，都不行就用軟體編碼器</summary>
        /// <param name="width">要是偶數</param>
        public static H264Encoder Create(int width, int height, int fps, int bitrate, bool hardware = true)
        {
            if (hardware && !hardwareDisabled)
            {
                IntPtr* list = EnumHardware(out uint count);
                if (list != null)
                {
                    H264Encoder found = null;
                    for (int i = 0; i < count; i++)
                    {
                        if (found != null) { Native.Release(ref list[i]); continue; }
                        string name = GetString(list[i], MFT_FRIENDLY_NAME_Attribute) ?? "硬體編碼器";
                        try { found = new H264Encoder(width, height, fps, bitrate, list[i], name); }
                        catch (Exception ex) { Logger.Warn($"[遠端畫面] 硬體編碼器 {name} 不能用: {ex.Message}"); }
                    }
                    CoTaskMemFree((IntPtr)list);
                    if (found != null) return found;
                }
            }
            return new H264Encoder(width, height, fps, bitrate);
        }

        /// <summary>Windows 內建的軟體編碼器</summary>
        /// <param name="width">要是偶數</param>
        public H264Encoder(int width, int height, int fps, int bitrate) : this(width, height, fps, bitrate, IntPtr.Zero, "軟體編碼器") { }

        // activate = 硬體編碼器 (MFTEnumEx 列出來的，不管成功失敗都交給這個物件釋放)；IntPtr.Zero = 軟體編碼器
        H264Encoder(int width, int height, int fps, int bitrate, IntPtr activate, string name)
        {
            Width = width;
            Height = height;
            Hardware = activate != IntPtr.Zero;
            Name = name;
            Fps = fps;
            Bitrate = bitrate;
            this.activate = activate;
            frameSize = width * height * 3 / 2;
            try
            {
                Startup();
                if (Hardware)
                {
                    Guid iid = IID_IMFTransform;
                    IntPtr t;
                    Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Native.Slot(activate, 33))(activate, &iid, &t), "建立 H.264 硬體編碼器"); // IMFActivate.ActivateObject
                    transform = t;
                }
                else Native.Check(CoCreateInstance(CLSID_MSH264Encoder, IntPtr.Zero, 1, IID_IMFTransform, out transform), "建立 H.264 編碼器");

                // 硬體編碼器都是非同步的：要先解鎖才能用，之後照它的事件 (要輸入 / 有輸出) 送資料
                IntPtr attrs;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Native.Slot(transform, 8))(transform, &attrs) >= 0 && attrs != IntPtr.Zero) // GetAttributes
                {
                    try
                    {
                        if (TryGetUInt32(attrs, MF_TRANSFORM_ASYNC, out uint isAsync) && isAsync != 0)
                        {
                            async = true;
                            SetUInt32(attrs, MF_TRANSFORM_ASYNC_UNLOCK, 1);
                        }
                        TrySetUInt32(attrs, MF_LOW_LATENCY, 1);
                    }
                    finally { Native.Release(ref attrs); }
                }
                if (async) Native.Check(Native.QueryInterface(transform, IID_IMFMediaEventGenerator, out events), "IMFMediaEventGenerator");

                // 串流編號：大部分是 0，有的硬體編碼器不是
                uint i, o;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, uint, uint*, int>)Native.Slot(transform, 5))(transform, 1, &i, 1, &o) >= 0) // GetStreamIDs (E_NOTIMPL = 都是 0)
                {
                    inStream = i;
                    outStream = o;
                }

                if (Native.QueryInterface(transform, IID_ICodecAPI, out IntPtr api) >= 0) codecApi = api;
                SetCodec(CODECAPI_AVEncCommonRateControlMode, 0); // CBR
                SetCodec(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
                SetCodec(CODECAPI_AVEncMPVGOPSize, (uint)(fps * 4));
                SetCodec(CODECAPI_AVEncCommonQualityVsSpeed, 25); // 偏快 (0 最快、100 最好)
                SetCodecBool(MF_LOW_LATENCY, true);

                // 編碼器要先設輸出格式再設輸入格式。Baseline 不收的硬體編碼器改試 Constrained Baseline
                int hr = 0;
                foreach (uint profile in Hardware ? new uint[] { 66, 256 } : new uint[] { 66 }) // eAVEncH264VProfile_Base、eAVEncH264VProfile_ConstrainedBase
                {
                    IntPtr type = NewVideoType(MFVideoFormat_H264, width, height, fps);
                    try
                    {
                        SetUInt32(type, MF_MT_AVG_BITRATE, (uint)bitrate);
                        SetUInt32(type, MF_MT_MPEG2_PROFILE, profile);
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 16))(transform, outStream, type, 0);
                    }
                    finally { Native.Release(ref type); }
                    if (hr >= 0) break;
                }
                Native.Check(hr, "SetOutputType");

                // 輸入：硬體編碼器幾乎都只收 NV12；軟體編碼器維持 I420
                nv12 = Hardware;
                IntPtr inType = NewVideoType(nv12 ? MFVideoFormat_NV12 : MFVideoFormat_IYUV, width, height, fps);
                try
                {
                    SetUInt32(inType, MF_MT_DEFAULT_STRIDE, (uint)width);
                    Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 15))(transform, inStream, inType, 0), "SetInputType");
                }
                finally { Native.Release(ref inType); }

                // 輸出的記憶體：硬體編碼器通常自己提供，軟體編碼器要我們準備
                StreamInfo si;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, StreamInfo*, int>)Native.Slot(transform, 7))(transform, outStream, &si), "GetOutputStreamInfo");
                providesSamples = (si.Flags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                if (!providesSamples)
                {
                    uint outSize = Math.Max(si.Size, (uint)frameSize);
                    Native.Check(MFCreateSample(out outSample), "MFCreateSample");
                    Native.Check(MFCreateMemoryBuffer(outSize, out outBuffer), "MFCreateMemoryBuffer");
                    Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Native.Slot(outSample, 42))(outSample, outBuffer), "AddBuffer");
                }

                Native.Check(Message(0x10000000), "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING");
                Native.Check(Message(0x10000003), "MFT_MESSAGE_NOTIFY_START_OF_STREAM");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>編碼途中改碼率 (自動畫質用)；編碼器不支援就回傳 false (呼叫端重建編碼器)</summary>
        public bool SetBitrate(int bitrate)
        {
            if (SetCodec(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate) < 0) return false;
            Bitrate = bitrate;
            return true;
        }

        /// <summary>編碼一張 (BGRA，大小要跟建立時一樣)；回傳 H.264 Annex B，編碼器還沒吐出東西時回傳 null</summary>
        public byte[] Encode(Mat bgra, bool keyFrame, long durationTicks)
        {
            if (keyFrame) SetCodec(CODECAPI_AVEncVideoForceKeyFrame, 1);

            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                Native.Check(MFCreateMemoryBuffer((uint)frameSize, out buffer), "MFCreateMemoryBuffer");
                byte* p;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Native.Slot(buffer, 3))(buffer, &p, null, null), "Lock");
                try { FillInput(bgra, p); }
                finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Native.Slot(buffer, 4))(buffer); } // Unlock
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Native.Slot(buffer, 6))(buffer, (uint)frameSize); // SetCurrentLength
                Native.Check(MFCreateSample(out sample), "MFCreateSample");
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Native.Slot(sample, 42))(sample, buffer); // AddBuffer
                ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Native.Slot(sample, 36))(sample, frameTime); // SetSampleTime (100ns)
                ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Native.Slot(sample, 38))(sample, durationTicks); // SetSampleDuration
                frameTime += durationTicks;

                var output = new MemoryStream();
                if (async) EncodeAsync(sample, output);
                else
                {
                    int hr = ProcessInput(sample);
                    if (hr == MF_E_NOTACCEPTING)
                    {
                        while (ReadOutput(output)) { }
                        hr = ProcessInput(sample);
                    }
                    Native.Check(hr, "ProcessInput");
                    while (ReadOutput(output)) { }
                }
                return output.Length == 0 ? null : AddSequenceHeader(output.ToArray());
            }
            finally
            {
                Native.Release(ref sample);
                Native.Release(ref buffer);
            }
        }

        // BGRA → I420 (軟體) 或 NV12 (硬體：U、V 交錯放在同一個平面)
        void FillInput(Mat bgra, byte* dst)
        {
            using var yuv = new Mat();
            Cv2.CvtColor(bgra, yuv, ColorConversionCodes.BGRA2YUV_I420);
            byte* src = (byte*)yuv.Data;
            int ySize = Width * Height, cSize = ySize / 4;
            if (!nv12)
            {
                Buffer.MemoryCopy(src, dst, frameSize, frameSize);
                return;
            }
            Buffer.MemoryCopy(src, dst, ySize, ySize);
            byte* u = src + ySize, v = u + cSize, uv = dst + ySize;
            for (int i = 0; i < cSize; i++)
            {
                uv[2 * i] = u[i];
                uv[2 * i + 1] = v[i];
            }
        }

        // 非同步 (硬體)：編碼器要輸入時才能送；送完等這張的輸出 (不等的話畫面停下來時最後一張會卡在編碼器裡)。
        // 有的編碼器固定晚幾張才吐：等不到就記下來 (lag)，之後不再空等
        void EncodeAsync(IntPtr sample, MemoryStream output)
        {
            if (!WaitFor(output, () => needInput > 0, 1000)) throw new TimeoutException("硬體編碼器沒有要求輸入");
            Native.Check(ProcessInput(sample), "ProcessInput");
            needInput--;
            inputs++;
            if (!WaitFor(output, () => outputs >= inputs - lag, 100) && lag < 3)
            {
                lag++;
                Logger.Info($"[遠端畫面] 硬體編碼器 {Name} 會晚 {lag} 張才輸出");
            }
        }

        // 處理編碼器的事件直到 done 成立或逾時；回傳 done 有沒有成立
        bool WaitFor(MemoryStream output, Func<bool> done, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                while (NextEvent(out uint type))
                {
                    if (type == METransformNeedInput) needInput++;
                    else if (type == METransformHaveOutput) ReadOutput(output);
                }
                if (done()) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Native.PreciseSleep(1);
            }
        }

        // 取一個事件 (不等)；沒有事件回傳 false，編碼器回報錯誤就丟例外
        bool NextEvent(out uint type)
        {
            type = 0;
            IntPtr ev;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Native.Slot(events, 3))(events, 1, &ev); // GetEvent (MF_EVENT_FLAG_NO_WAIT)
            if (hr == MF_E_NO_EVENTS_AVAILABLE) return false;
            Native.Check(hr, "GetEvent");
            try
            {
                uint t;
                int status;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Native.Slot(ev, 33))(ev, &t), "IMFMediaEvent.GetType");
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Native.Slot(ev, 35))(ev, &status), "IMFMediaEvent.GetStatus");
                Native.Check(status, $"硬體編碼器事件 {t}");
                type = t;
                return true;
            }
            finally { Native.Release(ref ev); }
        }

        int ProcessInput(IntPtr sample) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 24))(transform, inStream, sample, 0);

        // 取出一份輸出；回傳 false = 編碼器目前沒有輸出
        bool ReadOutput(MemoryStream output)
        {
            while (true)
            {
                if (!providesSamples) ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Native.Slot(outBuffer, 6))(outBuffer, 0); // SetCurrentLength
                var db = new OutputDataBuffer { StreamId = outStream, Sample = providesSamples ? IntPtr.Zero : outSample };
                uint status;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Native.Slot(transform, 25))(transform, 0, 1, &db, &status); // ProcessOutput
                if (db.Events != IntPtr.Zero) Native.Release(ref db.Events);
                try
                {
                    if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
                    if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                    {
                        // 編碼器要求重新確認輸出格式：用它提供的第一個
                        IntPtr type;
                        Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Native.Slot(transform, 14))(transform, outStream, 0, &type), "GetOutputAvailableType");
                        try { Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 16))(transform, outStream, type, 0), "SetOutputType"); }
                        finally { Native.Release(ref type); }
                        continue;
                    }
                    Native.Check(hr, "ProcessOutput");
                    if (!providesSamples) CopyBuffer(outBuffer, output);
                    else
                    {
                        IntPtr buffer;
                        Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Native.Slot(db.Sample, 41))(db.Sample, &buffer), "ConvertToContiguousBuffer");
                        try { CopyBuffer(buffer, output); }
                        finally { Native.Release(ref buffer); }
                    }
                    outputs++;
                    return true;
                }
                finally
                {
                    if (providesSamples) Native.Release(ref db.Sample);
                }
            }
        }

        static void CopyBuffer(IntPtr buffer, MemoryStream output)
        {
            byte* p;
            uint len;
            Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Native.Slot(buffer, 3))(buffer, &p, null, &len), "Lock");
            try { output.Write(new ReadOnlySpan<byte>(p, (int)len)); }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Native.Slot(buffer, 4))(buffer); }
        }

        // 記下 SPS / PPS；關鍵畫格 (IDR) 前面沒有就補上
        byte[] AddSequenceHeader(byte[] au)
        {
            var nals = NalTypes(au);
            if (nals.Contains(7) && nals.Contains(8))
            {
                int idr = IndexOfNal(au, 5);
                if (idr > 0) sequenceHeader = au[..idr];
                return au;
            }
            if (nals.Contains(5) && sequenceHeader != null) return [.. sequenceHeader, .. au];
            return au;
        }

        /// <summary>Annex B 裡每個 NAL 的種類 (5 = IDR、7 = SPS、8 = PPS)</summary>
        public static List<int> NalTypes(byte[] au)
        {
            var list = new List<int>();
            for (int i = 0; i + 3 < au.Length; i++)
                if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) { list.Add(au[i + 3] & 0x1F); i += 3; }
            return list;
        }

        // 某種 NAL 的起始碼位置 (含前面的 00 00 00 / 00 00 01)
        static int IndexOfNal(byte[] au, int type)
        {
            for (int i = 0; i + 3 < au.Length; i++)
                if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1 && (au[i + 3] & 0x1F) == type)
                    return i > 0 && au[i - 1] == 0 ? i - 1 : i;
            return -1;
        }

        IntPtr NewVideoType(in Guid subtype, int width, int height, int fps)
        {
            Native.Check(MFCreateMediaType(out IntPtr type), "MFCreateMediaType");
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(type, MF_MT_SUBTYPE, subtype);
            SetUInt64(type, MF_MT_FRAME_SIZE, ((ulong)width << 32) | (uint)height);
            SetUInt64(type, MF_MT_FRAME_RATE, ((ulong)fps << 32) | 1);
            SetUInt64(type, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            SetUInt32(type, MF_MT_INTERLACE_MODE, 2); // 逐行
            return type;
        }

        // IMFAttributes：GetUINT32 = 7、GetAllocatedString = 13、SetUINT32 = 21、SetUINT64 = 22、SetGUID = 24
        static bool TryGetUInt32(IntPtr attrs, in Guid key, out uint value)
        {
            uint v = 0;
            int hr;
            fixed (Guid* k = &key) hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Native.Slot(attrs, 7))(attrs, k, &v);
            value = v;
            return hr >= 0;
        }

        static string GetString(IntPtr attrs, in Guid key)
        {
            char* s;
            uint len;
            fixed (Guid* k = &key)
                if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, char**, uint*, int>)Native.Slot(attrs, 13))(attrs, k, &s, &len) < 0) return null;
            try { return new string(s, 0, (int)len); }
            finally { CoTaskMemFree((IntPtr)s); }
        }

        static int TrySetUInt32(IntPtr attrs, in Guid key, uint value)
        {
            fixed (Guid* k = &key) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Native.Slot(attrs, 21))(attrs, k, value);
        }

        static void SetUInt32(IntPtr attrs, in Guid key, uint value) => Native.Check(TrySetUInt32(attrs, key, value), "SetUINT32");

        static void SetUInt64(IntPtr attrs, in Guid key, ulong value)
        {
            fixed (Guid* k = &key) Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Native.Slot(attrs, 22))(attrs, k, value), "SetUINT64");
        }

        static void SetGuid(IntPtr attrs, in Guid key, in Guid value)
        {
            fixed (Guid* k = &key)
            fixed (Guid* v = &value)
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Native.Slot(attrs, 24))(attrs, k, v), "SetGUID");
        }

        // ICodecAPI.SetValue = 9；不支援的設定就算了 (用編碼器預設值)
        int SetCodec(in Guid key, uint value) => SetCodecValue(key, new Variant { Type = 19, UInt = value }); // VT_UI4
        int SetCodecBool(in Guid key, bool value) => SetCodecValue(key, new Variant { Type = 11, Bool = (short)(value ? -1 : 0) }); // VT_BOOL

        int SetCodecValue(in Guid key, Variant v)
        {
            if (codecApi == IntPtr.Zero) return unchecked((int)0x80004001); // E_NOTIMPL
            fixed (Guid* k = &key) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)Native.Slot(codecApi, 9))(codecApi, k, &v);
        }

        int Message(uint msg) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)Native.Slot(transform, 23))(transform, msg, 0);

        public void Dispose()
        {
            if (transform != IntPtr.Zero && async) Message(0); // MFT_MESSAGE_COMMAND_FLUSH：丟掉還在編碼器裡的畫面
            if (activate != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, int>)Native.Slot(activate, 34))(activate); // IMFActivate.ShutdownObject
            Native.Release(ref outBuffer);
            Native.Release(ref outSample);
            Native.Release(ref events);
            Native.Release(ref codecApi);
            Native.Release(ref transform);
            Native.Release(ref activate);
        }
    }
}
