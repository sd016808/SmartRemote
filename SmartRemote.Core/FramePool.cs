using OpenCvSharp;

namespace SmartRemote.Core
{
    /// <summary>
    /// 畫面用的記憶體重複使用 (截圖 → 編碼，一張 2560x1440 的 BGRA 是 15 MB)：用完 Return 而不是 Dispose，下一張 Rent 回來用。
    /// 截圖 (原尺寸) 跟縮小用的 (觀看頁顯示的大小、縮一半的暫存) 會輪流借：找大小一樣的給，不一樣的留著
    /// (以前大小不合就丟掉，縮小時兩種大小互相丟，每張都要重新配置)；最多留幾張，超過丟最舊的，所有觀看端共用
    /// </summary>
    static class FramePool
    {
        const int Max = 6;
        static readonly List<Mat> free = new();

        public static Mat Rent(int rows, int cols, MatType type)
        {
            lock (free)
            {
                for (int i = free.Count - 1; i >= 0; i--)
                {
                    Mat m = free[i];
                    if (m.Rows == rows && m.Cols == cols && m.Type() == type) { free.RemoveAt(i); return m; }
                }
            }
            return new Mat(rows, cols, type);
        }

        public static void Return(Mat m)
        {
            if (m == null || m.IsDisposed) return;
            Mat drop = null;
            lock (free)
            {
                free.Add(m);
                if (free.Count > Max) { drop = free[0]; free.RemoveAt(0); }
            }
            drop?.Dispose();
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
                foreach (Mat m in free) m.Dispose();
                free.Clear();
            }
        }
    }
}
