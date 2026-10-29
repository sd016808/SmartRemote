
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>
    /// 剪貼簿同步 (只有文字)：電腦這邊複製了東西就通知觀看端 (Changed)，觀看端傳來的文字放進電腦的剪貼簿 (SetText)。
    /// 寫入剪貼簿一定要有視窗 (OpenClipboard 傳 NULL 的話 SetClipboardData 會失敗)，所以開一條執行緒建立隱藏的訊息視窗，
    /// 剪貼簿的讀寫都在這條執行緒做
    /// </summary>
    static class RemoteClipboard
    {
        /// <summary>觀看端一次最多收 / 送幾個字 (DataChannel 單則訊息上限 256 KB)</summary>
        public const int MaxChars = 60000;

        static readonly BlockingCollection<Action> queue = new();
        static readonly object startLock = new();
        static Thread thread;
        static IntPtr window;
        static uint lastSequence;

        /// <summary>電腦的剪貼簿換成新的文字 (觀看端自己傳來的不算)</summary>
        public static event Action<string> Changed;

        public static bool DryRun { get; set; }

        static void EnsureStarted()
        {
            lock (startLock)
            {
                if (thread != null) return;
                thread = new Thread(Loop) { IsBackground = true, Name = "RemoteClipboard" };
                thread.Start();
            }
        }

        // 每 0.5 秒看一次剪貼簿序號有沒有變
        static void Loop()
        {
            window = CreateWindowEx(0, "STATIC", "SmartHunterClipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // HWND_MESSAGE
            lastSequence = GetClipboardSequenceNumber();
            while (true)
            {
                if (queue.TryTake(out Action action, 500))
                {
                    try { action(); }
                    catch (Exception ex) { Logger.Warn($"[遠端畫面] 剪貼簿處理失敗: {ex.Message}"); }
                    continue;
                }
                uint seq = GetClipboardSequenceNumber();
                if (seq == lastSequence || Changed == null) continue;
                lastSequence = seq;
                string text = ReadText();
                if (!string.IsNullOrEmpty(text)) Changed?.Invoke(text.Length > MaxChars ? text[..MaxChars] : text);
            }
        }

        /// <summary>開始監看 (有觀看端可以遙控時才開)</summary>
        public static void Watch(Action<string> handler)
        {
            EnsureStarted();
            // 連線前就在剪貼簿裡的不送，只送連上之後複製的
            queue.Add(() => { lastSequence = GetClipboardSequenceNumber(); Changed += handler; });
        }

        public static void Unwatch(Action<string> handler) => Changed -= handler;

        /// <summary>把觀看端傳來的文字放進電腦的剪貼簿</summary>
        public static void SetText(string text)
        {
            EnsureStarted();
            queue.Add(() =>
            {
                if (DryRun) { Logger.Info($"[遠端畫面][測試] 剪貼簿設成 {text.Length} 個字"); return; }
                if (!Open(window)) { Logger.Warn("[遠端畫面] 剪貼簿被其他程式佔用，沒有貼上"); return; }
                try
                {
                    EmptyClipboard();
                    // 要用 GlobalAlloc(GMEM_MOVEABLE)；SetClipboardData 成功後由系統接手，失敗才自己釋放
                    IntPtr mem = GlobalAlloc(2, (nuint)((text.Length + 1) * 2));
                    IntPtr p = GlobalLock(mem);
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    Marshal.WriteInt16(p, text.Length * 2, 0);
                    GlobalUnlock(mem);
                    if (SetClipboardData(13, mem) == IntPtr.Zero) GlobalFree(mem); // CF_UNICODETEXT
                }
                finally { CloseClipboard(); }
                lastSequence = GetClipboardSequenceNumber(); // 自己放的不要再傳回觀看端
            });
        }

        static string ReadText()
        {
            if (!Open(window)) return null;
            try
            {
                IntPtr h = GetClipboardData(13);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }

        // 別的程式正在用剪貼簿時 OpenClipboard 會失敗，稍等再試
        static bool Open(IntPtr owner)
        {
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(owner)) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    }
}
