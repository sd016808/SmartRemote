using OpenCvSharp;
using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>
    /// H.264 編碼 (Windows 內建 Media Foundation 的軟體編碼器，不用另外帶 dll)：BGRA 畫面 → H.264 (Annex B，給 WebRTC 的影像軌)。
    /// 用 Constrained Baseline、低延遲模式 (一張進一張出，沒有 B 畫格)，瀏覽器都能解。
    /// Windows N 版沒有裝媒體功能套件時建立會失敗，呼叫端改用 JPEG
    /// </summary>
    sealed unsafe class H264Encoder : IDisposable
    {
        public int Width { get; }
        public int Height { get; }

        IntPtr transform, codecApi, outSample, outBuffer;
        readonly int outSize;
        readonly byte[] i420;
        byte[] sequenceHeader; // SPS + PPS：每張關鍵畫格前面都要有 (中途加入 / 掉封包後才解得開)
        long frameTime;

        static readonly Guid CLSID_MSH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
        static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
        static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        static readonly Guid MFVideoFormat_IYUV = new("56555949-0000-0010-8000-00AA00389B71");
        static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
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

        [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
        [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr obj);

        [StructLayout(LayoutKind.Sequential)] struct StreamInfo { public uint Flags, Size, Alignment; }
        [StructLayout(LayoutKind.Sequential)] struct OutputDataBuffer { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public uint UInt; [FieldOffset(8)] public short Bool; }

        static readonly Lazy<int> started = new(() => MFStartup(0x20070, 0)); // MF_VERSION，MFSTARTUP_FULL

        /// <param name="width">要是偶數</param>
        public H264Encoder(int width, int height, int fps, int bitrate)
        {
            Width = width;
            Height = height;
            i420 = new byte[width * height * 3 / 2];
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED；已經初始化過也沒關係
            Native.Check(started.Value, "MFStartup");
            try
            {
                Native.Check(CoCreateInstance(CLSID_MSH264Encoder, IntPtr.Zero, 1, IID_IMFTransform, out transform), "建立 H.264 編碼器");

                IntPtr attrs;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Native.Slot(transform, 8))(transform, &attrs) >= 0 && attrs != IntPtr.Zero) // GetAttributes
                {
                    SetUInt32(attrs, MF_LOW_LATENCY, 1);
                    Native.Release(ref attrs);
                }
                if (Native.QueryInterface(transform, IID_ICodecAPI, out IntPtr api) >= 0) codecApi = api;
                SetCodec(CODECAPI_AVEncCommonRateControlMode, 0); // CBR
                SetCodec(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
                SetCodec(CODECAPI_AVEncMPVGOPSize, (uint)(fps * 4));
                SetCodec(CODECAPI_AVEncCommonQualityVsSpeed, 25); // 偏快 (0 最快、100 最好)
                SetCodecBool(MF_LOW_LATENCY, true);

                // 編碼器要先設輸出格式再設輸入格式
                IntPtr type = NewVideoType(MFVideoFormat_H264, width, height, fps);
                try
                {
                    SetUInt32(type, MF_MT_AVG_BITRATE, (uint)bitrate);
                    SetUInt32(type, MF_MT_MPEG2_PROFILE, 66); // eAVEncH264VProfile_Base
                    Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 16))(transform, 0, type, 0), "SetOutputType");
                }
                finally { Native.Release(ref type); }
                type = NewVideoType(MFVideoFormat_IYUV, width, height, fps);
                try { Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 15))(transform, 0, type, 0), "SetInputType"); }
                finally { Native.Release(ref type); }

                StreamInfo si;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, StreamInfo*, int>)Native.Slot(transform, 7))(transform, 0, &si), "GetOutputStreamInfo");
                outSize = (int)Math.Max(si.Size, (uint)(width * height * 3 / 2));
                Native.Check(MFCreateSample(out outSample), "MFCreateSample");
                Native.Check(MFCreateMemoryBuffer((uint)outSize, out outBuffer), "MFCreateMemoryBuffer");
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Native.Slot(outSample, 42))(outSample, outBuffer), "AddBuffer");

                Message(0x10000000); // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
                Message(0x10000003); // MFT_MESSAGE_NOTIFY_START_OF_STREAM
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>編碼一張 (BGRA，大小要跟建立時一樣)；回傳 H.264 Annex B，編碼器還沒吐出東西時回傳 null</summary>
        public byte[] Encode(Mat bgra, bool keyFrame, long durationTicks)
        {
            using (var yuv = new Mat())
            {
                Cv2.CvtColor(bgra, yuv, ColorConversionCodes.BGRA2YUV_I420);
                Marshal.Copy(yuv.Data, i420, 0, i420.Length);
            }
            if (keyFrame) SetCodec(CODECAPI_AVEncVideoForceKeyFrame, 1);

            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                Native.Check(MFCreateMemoryBuffer((uint)i420.Length, out buffer), "MFCreateMemoryBuffer");
                byte* p;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Native.Slot(buffer, 3))(buffer, &p, null, null), "Lock");
                Marshal.Copy(i420, 0, (IntPtr)p, i420.Length);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Native.Slot(buffer, 4))(buffer); // Unlock
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Native.Slot(buffer, 6))(buffer, (uint)i420.Length); // SetCurrentLength
                Native.Check(MFCreateSample(out sample), "MFCreateSample");
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Native.Slot(sample, 42))(sample, buffer); // AddBuffer
                ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Native.Slot(sample, 36))(sample, frameTime); // SetSampleTime (100ns)
                ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Native.Slot(sample, 38))(sample, durationTicks); // SetSampleDuration
                frameTime += durationTicks;

                var output = new MemoryStream();
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 24))(transform, 0, sample, 0); // ProcessInput
                if (hr == MF_E_NOTACCEPTING)
                {
                    Drain(output);
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 24))(transform, 0, sample, 0);
                }
                Native.Check(hr, "ProcessInput");
                Drain(output);
                return output.Length == 0 ? null : AddSequenceHeader(output.ToArray());
            }
            finally
            {
                Native.Release(ref sample);
                Native.Release(ref buffer);
            }
        }

        // 取出編碼器目前吐得出來的所有資料
        void Drain(MemoryStream output)
        {
            while (true)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Native.Slot(outBuffer, 6))(outBuffer, 0);
                var db = new OutputDataBuffer { Sample = outSample };
                uint status;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Native.Slot(transform, 25))(transform, 0, 1, &db, &status); // ProcessOutput
                if (db.Events != IntPtr.Zero) Native.Release(ref db.Events);
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    // 編碼器要求重新確認輸出格式：用它提供的第一個
                    IntPtr type;
                    Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Native.Slot(transform, 14))(transform, 0, 0, &type), "GetOutputAvailableType");
                    try { Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Native.Slot(transform, 16))(transform, 0, type, 0), "SetOutputType"); }
                    finally { Native.Release(ref type); }
                    continue;
                }
                Native.Check(hr, "ProcessOutput");
                byte* p;
                uint len;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Native.Slot(outBuffer, 3))(outBuffer, &p, null, &len), "Lock");
                try { output.Write(new ReadOnlySpan<byte>(p, (int)len)); }
                finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Native.Slot(outBuffer, 4))(outBuffer); }
            }
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

        // IMFAttributes：SetUINT32 = 21、SetUINT64 = 22、SetGUID = 24
        static void SetUInt32(IntPtr attrs, in Guid key, uint value)
        {
            fixed (Guid* k = &key) Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Native.Slot(attrs, 21))(attrs, k, value), "SetUINT32");
        }

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
        void SetCodec(in Guid key, uint value) => SetCodecValue(key, new Variant { Type = 19, UInt = value }); // VT_UI4
        void SetCodecBool(in Guid key, bool value) => SetCodecValue(key, new Variant { Type = 11, Bool = (short)(value ? -1 : 0) }); // VT_BOOL

        void SetCodecValue(in Guid key, Variant v)
        {
            if (codecApi == IntPtr.Zero) return;
            fixed (Guid* k = &key) ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)Native.Slot(codecApi, 9))(codecApi, k, &v);
        }

        void Message(uint msg) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)Native.Slot(transform, 23))(transform, msg, 0);

        public void Dispose()
        {
            Native.Release(ref outBuffer);
            Native.Release(ref outSample);
            Native.Release(ref codecApi);
            Native.Release(ref transform);
        }
    }
}
