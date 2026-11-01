
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>
    /// 截一個螢幕的畫面 (含滑鼠游標)，給遠端畫面用。
    /// 優先用 DXGI Desktop Duplication：直接從顯示卡拿畫面，快、而且知道畫面有沒有變 (沒變就不用重新編碼送出)；
    /// 這台電腦不支援 (例如筆電內顯 / 獨顯混用時螢幕不是接在這張顯示卡上) 就改用 GDI 截圖 (比較慢、每次都當作有變)。
    /// 必須在同一條執行緒建立與使用 (執行緒要先 Native.UsePerMonitorDpi)
    /// </summary>
    sealed unsafe class ScreenCapture : IDisposable
    {
        readonly Native.Monitor monitor;
        IntPtr device, context, duplication, staging;
        int width, height, rotation; // rotation：DXGI_MODE_ROTATION (1 = 不旋轉，2 = 90 度，3 = 180 度，4 = 270 度)
        Mat desktop;                 // 最近一次的桌面畫面 (BGRA，還沒轉正、沒有游標)
        bool useGdi;
        long nextRetryAt;

        // 游標：位置是游標圖左上角在這個螢幕裡的座標
        bool cursorVisible;
        int cursorX, cursorY;
        byte[] shape = Array.Empty<byte>();
        ShapeInfo shapeInfo;

        [StructLayout(LayoutKind.Sequential)] struct ShapeInfo { public uint Type, Width, Height, Pitch; public Native.POINT HotSpot; }

        /// <summary>目前截不到畫面的原因 (例如電腦正在顯示 UAC 確認視窗 / 鎖定畫面)；null = 正常</summary>
        public string Problem { get; private set; }

        public bool UsingGdi => useGdi;

        public ScreenCapture(Native.Monitor monitor)
        {
            this.monitor = monitor;
            try { Open(); }
            catch (COMException ex) when (ex.HResult == E_ACCESSDENIED)
            {
                // 現在是安全桌面 (UAC / 鎖定畫面)：之後還能用，Grab 會每秒重試
                Close();
                Problem = SecureDesktopText;
                Logger.Info($"[遠端畫面] 還不能截圖 (每秒重試): {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[遠端畫面] 螢幕 {monitor.Bounds} 不能用 Desktop Duplication，改用 GDI 截圖: {ex.Message}");
                Close();
                useGdi = true;
            }
        }

        /// <summary>
        /// 等畫面有變 (最多 timeoutMs)，有變就回傳新的一張 (BGRA，含游標，呼叫端負責 Dispose)；
        /// force = 沒變也要回傳最近一張 (定時補送、剛換畫質)。截不到 (安全桌面、還沒有任何畫面) 回傳 null
        /// </summary>
        public Mat Grab(int timeoutMs, bool force)
        {
            if (useGdi)
            {
                Thread.Sleep(timeoutMs);
                return GrabGdi();
            }
            bool changed = false;
            if (duplication == IntPtr.Zero)
            {
                // 重新建立 (畫面切到安全桌面 / 解析度改變之後)：每秒試一次
                if (Environment.TickCount64 < nextRetryAt) { Thread.Sleep(timeoutMs); return force ? Compose() : null; }
                try { Open(); Problem = null; changed = true; }
                catch (Exception ex)
                {
                    Close();
                    nextRetryAt = Environment.TickCount64 + 1000;
                    if (Problem == null)
                    {
                        Problem = SecureDesktopText;
                        Logger.Info($"[遠端畫面] 還不能截圖 (每秒重試): {ex.Message}");
                    }
                    Thread.Sleep(timeoutMs);
                    return force ? Compose() : null;
                }
            }
            try { changed |= Acquire(timeoutMs); }
            catch (COMException ex)
            {
                // DXGI_ERROR_ACCESS_LOST：切到安全桌面、解析度 / 螢幕設定改變、全螢幕程式切換，要重新建立
                Logger.Info($"[遠端畫面] 畫面擷取中斷，重新建立: {ex.Message}");
                Close();
                nextRetryAt = 0;
                return force ? Compose() : null;
            }
            return changed || force ? Compose() : null;
        }

        // ===== Desktop Duplication =====
        static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        const int E_ACCESSDENIED = unchecked((int)0x80070005);
        const string SecureDesktopText = "電腦正在顯示安全桌面 (UAC 確認視窗、鎖定畫面或登入畫面)，遠端看不到也不能操作";

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint levels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        struct OutputDesc { public fixed char DeviceName[32]; public Native.RECT Desktop; public int Attached, Rotation; public IntPtr Monitor; }

        [StructLayout(LayoutKind.Sequential)]
        struct DuplDesc { public uint Width, Height, RefreshNum, RefreshDen, Format, Scanline, Scaling, Rotation; public int InSystemMemory; }

        [StructLayout(LayoutKind.Sequential)]
        struct FrameInfo { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedMasked; public Native.POINT Pointer; public int PointerVisible; public uint MetadataSize, PointerShapeSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc; }

        [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }

        // 找到這個螢幕接在哪張顯示卡的哪個輸出，在那張顯示卡上建立 D3D11 裝置並開始複製畫面
        void Open()
        {
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
            try
            {
                Native.Check(CreateDXGIFactory1(IID_IDXGIFactory1, out factory), "CreateDXGIFactory1");
                for (uint a = 0; output == IntPtr.Zero; a++)
                {
                    IntPtr ad;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Native.Slot(factory, 12))(factory, a, &ad); // EnumAdapters1
                    if (hr == DXGI_ERROR_NOT_FOUND) throw new InvalidOperationException("找不到這個螢幕對應的顯示卡輸出");
                    Native.Check(hr, "EnumAdapters1");
                    for (uint o = 0; ; o++)
                    {
                        IntPtr op;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Native.Slot(ad, 7))(ad, o, &op) < 0) break; // EnumOutputs
                        OutputDesc desc;
                        ((delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)Native.Slot(op, 7))(op, &desc); // GetDesc
                        if (desc.Monitor == monitor.Handle) { output = op; adapter = ad; break; }
                        Native.Release(ref op);
                    }
                    if (output == IntPtr.Zero) Native.Release(ref ad);
                }

                // D3D_DRIVER_TYPE_UNKNOWN (指定顯示卡時要用這個)、D3D11_CREATE_DEVICE_BGRA_SUPPORT
                Native.Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context), "D3D11CreateDevice");
                Native.Check(Native.QueryInterface(output, IID_IDXGIOutput1, out output1), "IDXGIOutput1");
                IntPtr dup;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Native.Slot(output1, 22))(output1, device, &dup), "DuplicateOutput");
                duplication = dup;
                DuplDesc dd;
                ((delegate* unmanaged[Stdcall]<IntPtr, DuplDesc*, void>)Native.Slot(duplication, 7))(duplication, &dd); // GetDesc
                width = (int)dd.Width;
                height = (int)dd.Height;
                rotation = (int)dd.Rotation;

                var td = new TextureDesc { Width = dd.Width, Height = dd.Height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3, CpuAccess = 0x20000 }; // B8G8R8A8、STAGING、CPU 讀取
                IntPtr tex;
                Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)Native.Slot(device, 5))(device, &td, IntPtr.Zero, &tex), "CreateTexture2D");
                staging = tex;
                if (desktop == null || desktop.Width != width || desktop.Height != height)
                {
                    desktop?.Dispose();
                    desktop = new Mat(height, width, MatType.CV_8UC4, Scalar.Black);
                }
            }
            finally
            {
                Native.Release(ref output1);
                Native.Release(ref output);
                Native.Release(ref adapter);
                Native.Release(ref factory);
            }
        }

        // 等下一張畫面；回傳 true = 桌面或游標有變
        bool Acquire(int timeoutMs)
        {
            FrameInfo info;
            IntPtr resource;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)Native.Slot(duplication, 8))(duplication, (uint)timeoutMs, &info, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
            Native.Check(hr, "AcquireNextFrame");
            try
            {
                if (info.LastPresentTime != 0)
                {
                    Native.Check(Native.QueryInterface(resource, IID_ID3D11Texture2D, out IntPtr texture), "ID3D11Texture2D");
                    try { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Native.Slot(context, 47))(context, staging, texture); } // CopyResource
                    finally { Native.Release(ref texture); }
                    Mapped m;
                    Native.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)Native.Slot(context, 14))(context, staging, 0, 1, 0, &m), "Map");
                    try
                    {
                        using var src = Mat.FromPixelData(height, width, MatType.CV_8UC4, m.Data, m.RowPitch);
                        src.CopyTo(desktop);
                    }
                    finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Native.Slot(context, 15))(context, staging, 0); } // Unmap
                }
                if (info.LastMouseUpdateTime != 0)
                {
                    cursorVisible = info.PointerVisible != 0;
                    cursorX = info.Pointer.X;
                    cursorY = info.Pointer.Y;
                }
                if (info.PointerShapeSize > 0)
                {
                    if (shape.Length < info.PointerShapeSize) shape = new byte[info.PointerShapeSize];
                    uint required;
                    ShapeInfo si;
                    fixed (byte* buf = shape)
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, uint*, ShapeInfo*, int>)Native.Slot(duplication, 11))(duplication, (uint)shape.Length, buf, &required, &si);
                    if (hr >= 0) shapeInfo = si;
                }
                return info.LastPresentTime != 0 || info.LastMouseUpdateTime != 0;
            }
            finally
            {
                Native.Release(ref resource);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Native.Slot(duplication, 14))(duplication); // ReleaseFrame
            }
        }

        // 桌面 (轉正) + 游標
        Mat Compose()
        {
            if (desktop == null) return null;
            Mat img;
            if (rotation is 2 or 3 or 4)
            {
                img = new Mat();
                Cv2.Rotate(desktop, img, rotation == 2 ? RotateFlags.Rotate90Clockwise : rotation == 3 ? RotateFlags.Rotate180 : RotateFlags.Rotate90Counterclockwise);
            }
            else img = desktop.Clone();
            if (cursorVisible && shapeInfo.Type != 0) DrawCursor(img);
            return img;
        }

        // 游標三種格式：1 = 黑白 (上半 AND 遮罩、下半 XOR)，2 = 彩色 (有透明度)，4 = 彩色 + XOR 遮罩 (alpha 0 = 直接蓋上，0xFF = 跟底色 XOR)
        void DrawCursor(Mat img)
        {
            int w = (int)shapeInfo.Width, h = (int)(shapeInfo.Type == 1 ? shapeInfo.Height / 2 : shapeInfo.Height), pitch = (int)shapeInfo.Pitch;
            var px = img.GetGenericIndexer<Vec4b>();
            for (int y = 0; y < h; y++)
            {
                int dy = cursorY + y;
                if (dy < 0 || dy >= img.Height) continue;
                for (int x = 0; x < w; x++)
                {
                    int dx = cursorX + x;
                    if (dx < 0 || dx >= img.Width) continue;
                    Vec4b d = px[dy, dx];
                    if (shapeInfo.Type == 1)
                    {
                        int bit = 0x80 >> (x & 7);
                        bool and = (shape[y * pitch + x / 8] & bit) != 0, xor = (shape[(y + h) * pitch + x / 8] & bit) != 0;
                        if (and && !xor) continue;
                        byte v = xor ? (byte)255 : (byte)0;
                        px[dy, dx] = and ? new Vec4b((byte)~d.Item0, (byte)~d.Item1, (byte)~d.Item2, 255) : new Vec4b(v, v, v, 255);
                    }
                    else
                    {
                        int i = y * pitch + x * 4;
                        byte b = shape[i], g = shape[i + 1], r = shape[i + 2], a = shape[i + 3];
                        if (shapeInfo.Type == 4)
                            px[dy, dx] = a == 0 ? new Vec4b(b, g, r, 255) : new Vec4b((byte)(d.Item0 ^ b), (byte)(d.Item1 ^ g), (byte)(d.Item2 ^ r), 255);
                        else if (a != 0)
                            px[dy, dx] = new Vec4b((byte)((b * a + d.Item0 * (255 - a)) / 255), (byte)((g * a + d.Item1 * (255 - a)) / 255), (byte)((r * a + d.Item2 * (255 - a)) / 255), 255);
                    }
                }
            }
        }

        // ===== GDI 截圖 (Desktop Duplication 不能用時) =====
        Mat GrabGdi()
        {
            Rect b = monitor.Bounds;
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(b.X, b.Y, 0, 0, new System.Drawing.Size(b.Width, b.Height), System.Drawing.CopyPixelOperation.SourceCopy);
                var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
                if (Native.GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && Native.GetIconInfo(ci.hCursor, out var ii))
                {
                    IntPtr hdc = g.GetHdc();
                    try { Native.DrawIconEx(hdc, ci.pt.X - b.X - ii.xHotspot, ci.pt.Y - b.Y - ii.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); } // DI_NORMAL
                    finally
                    {
                        g.ReleaseHdc(hdc);
                        if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                        if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                    }
                }
            }
            return BitmapConverter.ToMat(bmp);
        }

        void Close()
        {
            Native.Release(ref staging);
            Native.Release(ref duplication);
            Native.Release(ref context);
            Native.Release(ref device);
        }

        public void Dispose()
        {
            Close();
            desktop?.Dispose();
            desktop = null;
        }
    }
}
