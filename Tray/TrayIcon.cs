using SmartRemote.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartRemote.Tray
{
    /// <summary>
    /// 系統匣介面，直接呼叫 Win32 (不用 WinForms，WinForms 不能 Native AOT)。
    /// 右鍵圖示看房間碼與密碼、複製網址、開關遙控、中斷連線；雙擊圖示 = 複製觀看網址。
    /// 不跳任何通知。
    /// </summary>
    internal sealed unsafe class TrayIcon
    {
        const uint WM_LBUTTONDBLCLK = 0x0203;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_NULL = 0x0000;
        const uint WM_APP_INVOKE = 0x8000;   // 背景執行緒丟動作回來用
        const uint WM_TRAY = 0x0401;         // WM_USER + 1，系統匣圖示的訊息
        const uint WS_POPUP = 0x80000000;

        const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
        const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
        const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
        const uint GMEM_MOVEABLE = 0x0002, CF_UNICODETEXT = 13;

        const int CmdNone = 0, CmdCopyCode = 1, CmdCopyUrl = 2, CmdControl = 3, CmdPause = 4, CmdRecord = 5, CmdSettings = 6, CmdLogs = 7, CmdExit = 8;

        static readonly ConcurrentQueue<Action> queue = new();
        static TrayIcon current; // 視窗程序是靜態函式，要用這個找到實例

        readonly IntPtr hwnd;
        readonly string room, password;
        bool exiting;

        public TrayIcon(Settings settings)
        {
            current = this;
            room = settings.Room != "" ? settings.Room : RemoteView.RandomCode(8);
            password = settings.Password != "" ? settings.Password : RemoteView.RandomCode(10);

            IntPtr instance = GetModuleHandleW(IntPtr.Zero);
            IntPtr className = Marshal.StringToHGlobalUni("SmartRemoteTray");   // 程式結束前一直用，不釋放
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (RegisterClassExW(&wc) == 0) throw new InvalidOperationException($"註冊視窗類別失敗 (Windows 錯誤碼 {Marshal.GetLastPInvokeError()})");
            hwnd = CreateWindowExW(0, className, className, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"建立隱藏視窗失敗 (Windows 錯誤碼 {Marshal.GetLastPInvokeError()})");

            var nid = Notify(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            if (!Shell_NotifyIconW(NIM_ADD, &nid)) Logger.Warn($"系統匣圖示建立失敗 (Windows 錯誤碼 {Marshal.GetLastPInvokeError()})");

            RemoteView.StateChanged += () => Post(UpdateTip);
            UpdateTip();

            // SMARTREMOTE_DRYRUN=1 只給測試用：遙控的輸入與剪貼簿只寫日誌，不真的動滑鼠鍵盤
            RemoteView.Start(room, password, settings.Control, Environment.GetEnvironmentVariable("SMARTREMOTE_DRYRUN") == "1");
            Logger.Info($"SmartRemote 啟動：房間碼 {room}，{(settings.Control ? "可遙控" : "只能看")}");
        }

        /// <summary>訊息迴圈，直到選「結束」為止</summary>
        public void Run()
        {
            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        // 從任何執行緒丟一個動作回到這條執行緒執行
        void Post(Action action)
        {
            queue.Enqueue(action);
            PostMessageW(hwnd, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }

        [UnmanagedCallersOnly]
        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var self = current;
            if (self == null) return DefWindowProcW(hWnd, msg, wParam, lParam);
            switch (msg)
            {
                case WM_TRAY:
                    uint mouse = (uint)((long)lParam & 0xFFFF);
                    if (mouse == WM_RBUTTONUP) self.ShowMenu();
                    else if (mouse == WM_LBUTTONDBLCLK) self.Dispatch(CmdCopyUrl);
                    return IntPtr.Zero;
                case WM_APP_INVOKE:
                    while (queue.TryDequeue(out var action))
                    {
                        try { action(); }
                        catch (Exception ex) { Logger.Warn($"介面更新失敗: {ex.Message}"); }
                    }
                    return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // 右鍵選單：每次開啟時重建，標題與勾選狀態才會是最新的
        void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            Add(menu, MF_STRING | MF_GRAYED, 0, $"房間碼 {room}　密碼 {password}");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING, CmdCopyCode, "複製房間碼與密碼");
            Add(menu, MF_STRING, CmdCopyUrl, "複製觀看網址 (含房間碼與密碼)");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING | (RemoteView.AllowControl ? MF_CHECKED : 0), CmdControl, "允許遙控");
            Add(menu, MF_STRING | (RemoteView.Paused ? MF_CHECKED : 0), CmdPause, "中斷所有連線並暫停接受");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING, CmdRecord, "開啟連線紀錄");
            Add(menu, MF_STRING, CmdSettings, "開啟設定檔");
            Add(menu, MF_STRING, CmdLogs, "開啟日誌資料夾");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING, CmdExit, "結束");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(hwnd);   // 不這樣做選單點外面不會關
            int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, IntPtr.Zero);
            DestroyMenu(menu);
            PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != CmdNone) Dispatch(cmd);
        }

        void Dispatch(int cmd)
        {
            switch (cmd)
            {
                case CmdCopyCode: SetClipboardText($"房間碼：{room}\n密碼：{password}"); break;
                case CmdCopyUrl: SetClipboardText($"{RemoteView.ViewerUrl}#{room}/{Uri.EscapeDataString(password)}"); break;
                case CmdControl: RemoteView.SetControl(!RemoteView.AllowControl); break;
                case CmdPause: RemoteView.TogglePause(); break;
                case CmdRecord: OpenPath(RemoteLog.FilePath); break;
                case CmdSettings: OpenPath(Settings.FilePath); break;
                case CmdLogs: OpenPath(AppPaths.InBaseDir("logs")); break;
                case CmdExit: Exit(); break;
            }
        }

        // 狀態改變時更新滑鼠停在圖示上面看到的文字
        void UpdateTip()
        {
            var nid = Notify(NIF_TIP);
            string text = $"SmartRemote：{RemoteView.Status}";
            SetTip(ref nid, text.Length > 127 ? text[..127] : text);
            Shell_NotifyIconW(NIM_MODIFY, &nid);
        }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            var nid = Notify(0);
            Shell_NotifyIconW(NIM_DELETE, &nid);
            DestroyWindow(hwnd);
            PostQuitMessage(0);
        }

        NOTIFYICONDATAW Notify(uint flags)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)sizeof(NOTIFYICONDATAW),
                hWnd = hwnd,
                uID = 1,
                uFlags = flags,
                uCallbackMessage = WM_TRAY,
                hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32518), // IDI_SHIELD (舊系統沒有就會是空的，下面再換成預設圖示)
            };
            if (nid.hIcon == IntPtr.Zero) nid.hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            SetTip(ref nid, "SmartRemote");
            return nid;
        }

        static void SetTip(ref NOTIFYICONDATAW nid, string text)
        {
            for (int i = 0; i < 128; i++) nid.szTip[i] = '\0';
            for (int i = 0; i < text.Length && i < 127; i++) nid.szTip[i] = text[i];
        }

        static void Add(IntPtr menu, uint flags, int id, string text)
        {
            IntPtr p = text == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(text);
            try { AppendMenuW(menu, flags, (UIntPtr)(uint)id, p); }
            finally { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
        }

        static void SetClipboardText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Logger.Warn("剪貼簿被其他程式佔用，沒有複製"); return; }
            try
            {
                EmptyClipboard();
                IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
                IntPtr p = GlobalLock(mem);
                text.AsSpan().CopyTo(new Span<char>((void*)p, text.Length));
                ((char*)p)[text.Length] = '\0';
                GlobalUnlock(mem);
                if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero) GlobalFree(mem);
            }
            finally { CloseClipboard(); }
        }

        static void OpenPath(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) path = Path.GetDirectoryName(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            }
            catch (Exception ex) { Logger.Warn($"無法開啟 {path}: {ex.Message}"); }
        }

        // ===== Win32 =====
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [StructLayout(LayoutKind.Sequential)]
        struct WNDCLASSEXW
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID, uFlags, uCallbackMessage;
            public IntPtr hIcon;
            public fixed char szTip[128];
            public uint dwState, dwStateMask;
            public fixed char szInfo[256];
            public uint uTimeoutOrVersion;
            public fixed char szInfoTitle[64];
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateWindowExW(uint exStyle, IntPtr className, IntPtr windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", SetLastError = true)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, IntPtr text);
        [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("shell32.dll", SetLastError = true)] static extern bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetModuleHandleW(IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
    }
}
