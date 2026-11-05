using OpenCvSharp;

namespace SmartRemote.Core
{
    /// <summary>
    /// 一個觀看端看的螢幕畫面 (含滑鼠游標)。同一個螢幕在這個程式裡只截一份 (CaptureSource，自己一條執行緒)，
    /// 所有觀看端共用：同一個程式同時對同一個螢幕開兩份 Desktop Duplication 會失敗 (E_INVALIDARG)，
    /// 以前第二個觀看端 (同時看、或重新連線時舊的還沒斷乾淨) 就只能改用 GDI，又慢又常卡在第一張。
    /// 每個觀看端拿到的是自己的一份複本 (用完 FramePool.Return)
    /// </summary>
    sealed class ScreenCapture : IDisposable
    {
        readonly CaptureSource source;
        long seen; // 這個觀看端拿過的最後一張的編號 (0 = 還沒拿過；第一張是 1)
        bool disposed;

        public ScreenCapture(Native.Monitor monitor) => source = CaptureSource.Acquire(monitor);

        /// <summary>目前截不到畫面的原因 (例如電腦正在顯示 UAC 確認視窗 / 鎖定畫面)；null = 正常</summary>
        public string Problem => source.Problem;
        public bool UsingGdi => source.UsingGdi;

        /// <summary>最近一次 Grab 回傳的畫面是新的 (畫面或游標有變)，不是 force 補送的</summary>
        public bool Changed { get; private set; }

        /// <summary>
        /// 等新的畫面 (最多 timeoutMs)，有就回傳 (BGRA，含游標，呼叫端負責 Dispose 或 FramePool.Return)；
        /// force = 沒有新的也回傳最近一張 (定時補送、剛換畫質)。還沒有任何畫面回傳 null
        /// </summary>
        public Mat Grab(int timeoutMs, bool force)
        {
            Mat img = source.Next(ref seen, timeoutMs, force, out bool changed);
            Changed = changed;
            return img;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            source.Release();
        }
    }

    /// <summary>一個螢幕的截圖來源：自己一條執行緒一直截 (DesktopCapturer)，有新的一張就給等著的觀看端；沒人用了就關掉</summary>
    sealed class CaptureSource
    {
        static readonly Dictionary<IntPtr, CaptureSource> sources = new();

        readonly Native.Monitor monitor;
        readonly object gate = new();
        int users;
        volatile bool stopping;
        Mat latest;   // 最近一張 (畫面 + 游標)
        long seq;     // latest 的編號，每張新的加一
        volatile string problem;
        volatile bool gdi;

        public string Problem => problem;
        public bool UsingGdi => gdi;

        CaptureSource(Native.Monitor monitor)
        {
            this.monitor = monitor;
            new Thread(Run) { IsBackground = true, Name = "ScreenCapture" }.Start();
        }

        public static CaptureSource Acquire(Native.Monitor monitor)
        {
            lock (sources)
            {
                if (!sources.TryGetValue(monitor.Handle, out var src) || src.stopping)
                {
                    src = new CaptureSource(monitor);
                    sources[monitor.Handle] = src;
                }
                src.users++;
                return src;
            }
        }

        public void Release()
        {
            lock (sources)
            {
                if (--users > 0) return;
                stopping = true;
                if (sources.TryGetValue(monitor.Handle, out var src) && src == this) sources.Remove(monitor.Handle);
            }
            lock (gate) Monitor.PulseAll(gate);
        }

        void Run()
        {
            Native.UsePerMonitorDpi();
            DesktopCapturer capturer = null;
            try
            {
                capturer = new DesktopCapturer(monitor);
                gdi = capturer.UsingGdi;
                problem = capturer.Problem;
                while (!stopping)
                {
                    // GDI 不知道畫面有沒有變：每次都截 (間隔短一點，不然每秒只有幾張)
                    Mat img = capturer.Grab(capturer.UsingGdi ? 16 : 100, false);
                    problem = capturer.Problem;
                    gdi = capturer.UsingGdi;
                    if (img == null) continue;
                    lock (gate)
                    {
                        FramePool.Return(latest);
                        latest = img;
                        seq++;
                        Monitor.PulseAll(gate);
                    }
                }
            }
            catch (Exception ex)
            {
                problem = $"截圖失敗：{ex.Message}";
                Logger.Warn($"[遠端畫面] 螢幕 {monitor.Bounds.Width}x{monitor.Bounds.Height} 截圖執行緒結束: {ex.Message}");
            }
            finally
            {
                capturer?.Dispose();
                lock (gate)
                {
                    FramePool.Return(latest);
                    latest = null;
                }
                FramePool.Clear();
            }
        }

        /// <summary>給觀看端：比 seen 新的一張 (等最多 timeoutMs)；force = 沒有新的也給最近一張</summary>
        public Mat Next(ref long seen, int timeoutMs, bool force, out bool changed)
        {
            changed = false;
            long end = Environment.TickCount64 + Math.Max(0, timeoutMs);
            lock (gate)
            {
                while (seq <= seen && !stopping)
                {
                    long left = end - Environment.TickCount64;
                    if (left <= 0) break;
                    Monitor.Wait(gate, (int)left);
                }
                if (latest == null || (seq <= seen && !force)) return null;
                changed = seq > seen;
                seen = seq;
                Mat copy = FramePool.Rent(latest.Rows, latest.Cols, latest.Type());
                latest.CopyTo(copy);
                return copy;
            }
        }
    }
}
