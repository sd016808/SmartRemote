using OpenCvSharp;

namespace SmartRemote.Core
{
    /// <summary>
    /// 畫面用的記憶體重複使用 (截圖 → 編碼，一張 2560x1440 的 BGRA 是 15 MB)：用完 Return 而不是 Dispose，下一張 Rent 回來用。
    /// 大小不合的 (換螢幕、換畫質) 直接丟掉；最多留幾張，所有觀看端共用
    /// </summary>
    static class FramePool
    {
        const int Max = 4;
        static readonly Stack<Mat> free = new();

        public static Mat Rent(int rows, int cols, MatType type)
        {
            lock (free)
            {
                while (free.Count > 0)
                {
                    Mat m = free.Pop();
                    if (m.Rows == rows && m.Cols == cols && m.Type() == type) return m;
                    m.Dispose();
                }
            }
            return new Mat(rows, cols, type);
        }

        public static void Return(Mat m)
        {
            if (m == null || m.IsDisposed) return;
            lock (free)
            {
                if (free.Count < Max) { free.Push(m); return; }
            }
            m.Dispose();
        }

        /// <summary>離開目前的範圍時還回來：using var lease = FramePool.ReturnLater(mat) (mat 可以是 null)</summary>
        public static Lease ReturnLater(Mat m) => new Lease(m);

        public readonly struct Lease : IDisposable
        {
            readonly Mat mat;
            public Lease(Mat mat) => this.mat = mat;
            public void Dispose() => Return(mat);
        }

        /// <summary>沒有人在看了：把留著的記憶體還給系統</summary>
        public static void Clear()
        {
            lock (free)
            {
                while (free.Count > 0) free.Pop().Dispose();
            }
        }
    }
}
