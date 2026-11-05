using SmartRemote.App;
using SmartRemote.Core;
using SmartRemote.Service;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartRemote.Tray
{
    /// <summary>
    /// 系統匣介面，直接呼叫 Win32 (不用 WinForms，WinForms 不能 Native AOT)。
    /// 右鍵圖示看房間碼與密碼、複製網址、開關遙控、中斷連線；雙擊圖示 = 複製觀看網址。
    /// 不跳任何通知。真正在連線的是 ITrayEngine：一般模式是這個程式自己，服務模式是系統服務開的遠端程式
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

        const int CmdNone = 0, CmdCopyCode = 1, CmdCopyUrl = 2, CmdControl = 3, CmdPause = 4, CmdRecord = 5, CmdSettings = 6, CmdLogs = 7, CmdExit = 8, CmdUpdate = 9, CmdAutoStart = 10, CmdService = 11, CmdRestartService = 12;

        static readonly ConcurrentQueue<Action> queue = new();
        static TrayIcon current; // 視窗程序是靜態函式，要用這個找到實例

        readonly IntPtr hwnd;
        readonly ITrayEngine engine;
        bool exiting;
        bool autoStart;   // 開機自動啟動 (設定檔 autoStart，右鍵選單切換)

        public TrayIcon(Settings settings, ITrayEngine engine)
        {
            current = this;
            this.engine = engine;
            autoStart = settings.AutoStart;

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

            engine.StateChanged += () => Post(UpdateTip);
            UpdateTip();
        }

        /// <summary>從外部結束 (定時更新換好檔案後用)：移除圖示並結束訊息迴圈</summary>
        public void RequestExit() => Post(Exit);

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
            bool known = engine.Room != "";
            bool service = engine.IsService;
            Add(menu, MF_STRING | MF_GRAYED, 0, known ? $"房間碼 {engine.Room}　密碼 {engine.Password}" : "房間碼讀取中… (遠端服務還沒回應)");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING | (known ? 0 : MF_GRAYED), CmdCopyCode, "複製房間碼與密碼");
            Add(menu, MF_STRING | (known ? 0 : MF_GRAYED), CmdCopyUrl, "複製觀看網址 (含房間碼與密碼)");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING | (engine.AllowControl ? MF_CHECKED : 0), CmdControl, "允許遙控");
            Add(menu, MF_STRING | (engine.Paused ? MF_CHECKED : 0), CmdPause, "中斷所有連線並暫停接受");
            Add(menu, MF_STRING, CmdUpdate, "檢查更新");
            Add(menu, MF_STRING | (service ? MF_CHECKED : 0), CmdService, "以系統服務執行 (可操作 UAC、鎖定畫面、登入畫面)");
            Add(menu, MF_STRING | (autoStart ? MF_CHECKED : 0), CmdAutoStart, service ? "登入時顯示這個圖示" : "開機自動啟動");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING, CmdRecord, "開啟連線紀錄");
            Add(menu, MF_STRING, CmdSettings, "開啟設定檔");
            Add(menu, MF_STRING, CmdLogs, "開啟日誌資料夾");
            if (service) Add(menu, MF_STRING, CmdRestartService, "重新啟動遠端服務 (改了設定檔後套用)");
            Add(menu, MF_SEPARATOR, 0, null);
            Add(menu, MF_STRING, CmdExit, service ? "關閉圖示 (遠端服務繼續執行)" : "結束");

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
                case CmdCopyCode: if (engine.Room != "") SetClipboardText($"房間碼：{engine.Room}\n密碼：{engine.Password}"); break;
                case CmdCopyUrl: if (engine.Room != "") SetClipboardText($"{RemoteView.ViewerUrl}#{engine.Room}/{Uri.EscapeDataString(engine.Password)}"); break;
                case CmdControl: engine.SetControl(!engine.AllowControl); break;
                case CmdPause: engine.TogglePause(); break;
                case CmdService: ToggleService(); break;
                case CmdRestartService: RestartService(); break;
                case CmdRecord: OpenPath(RemoteLog.FilePath); break;
                case CmdSettings: OpenSettings(); break;
                case CmdLogs: OpenPath(AppPaths.InBaseDir("logs")); break;
                case CmdUpdate: CheckForUpdate(); break;
                case CmdAutoStart: ToggleAutoStart(); break;
                case CmdExit: Exit(); break;
            }
        }

        // 手動檢查更新 (只有這裡會檢查)：在背景查，結果跳對話框；有新版先問要不要更新，按「是」才換檔、重新啟動。
        // 對話框在背景執行緒跳 (會等使用者按)，系統匣照常運作
        int updating;

        void CheckForUpdate()
        {
            if (Interlocked.Exchange(ref updating, 1) == 1) return; // 上一次的還沒結束 (對話框還開著)
            ShowTip("檢查更新中…");
            new Thread(() =>
            {
                const string Title = "SmartRemote 檢查更新";
                try
                {
                    AutoUpdater.Pending update = AutoUpdater.Check(out string error);
                    Post(UpdateTip);
                    if (error != null)
                    {
                        Win32.Message($"檢查更新失敗，請確認網路連線後再試一次。\n\n{error}", Title, Win32.MB_ICONERROR);
                        return;
                    }
                    if (update == null)
                    {
                        Win32.Message($"已是最新版 (v{AutoUpdater.CurrentVersion})。", Title, Win32.MB_ICONINFORMATION);
                        return;
                    }

                    string notes = string.IsNullOrEmpty(update.Notes) ? "" : $"\n\n更新內容：\n{(update.Notes.Length > 800 ? update.Notes[..800] + "…" : update.Notes)}";
                    int viewers = engine.Connections;
                    string warn = viewers > 0 ? $"\n\n目前有 {viewers} 個人連線中，更新時程式會重新啟動，連線會中斷。" : "\n\n更新時程式會重新啟動。";
                    if (Win32.Message($"有新版 v{update.Version} (目前 v{AutoUpdater.CurrentVersion})，要現在更新嗎？{notes}{warn}",
                                      Title, Win32.MB_YESNO | Win32.MB_ICONQUESTION) != Win32.IDYES)
                        return;

                    Post(() => ShowTip($"下載更新 v{update.Version} 中…"));
                    if (!AutoUpdater.Install(update))
                    {
                        Post(UpdateTip);
                        Win32.Message("更新失敗，已還原成原本的版本。詳細原因請看日誌資料夾 (右鍵圖示 →「開啟日誌資料夾」)。", Title, Win32.MB_ICONERROR);
                        return;
                    }
                    // 服務模式：換好的檔案跟服務是同一份 (Program Files)，重新啟動服務才會用新版
                    if (engine.IsService)
                    {
                        try { ServiceSetup.Restart(); }
                        catch (Exception ex) { Logger.Warn($"更新後重新啟動系統服務失敗: {ex.Message}"); }
                    }
                    Post(() =>
                    {
                        AutoUpdater.Relaunch();
                        Exit();
                    });
                }
                finally { Interlocked.Exchange(ref updating, 0); }
            }) { IsBackground = true, Name = "ManualUpdate" }.Start();
        }

        // ===== 系統服務 =====
        int serviceBusy;

        // 安裝 / 移除系統服務 (先問)。安裝完從 Program Files 重新開系統匣程式 (服務模式)，移除完重新開 (一般模式)
        void ToggleService()
        {
            if (Interlocked.Exchange(ref serviceBusy, 1) == 1) return;
            bool install = !engine.IsService;
            new Thread(() =>
            {
                const string Title = "SmartRemote 系統服務";
                try
                {
                    string ask = install
                        ? "安裝成系統服務後：\n" +
                          "• UAC 確認視窗、鎖定畫面、登入畫面都能遠端看和操作\n" +
                          "• 開機就會啟動，還沒有人登入 Windows 也能連 (知道房間碼和密碼的人就能看到登入畫面)\n" +
                          $"• 程式會複製到 {ServiceSetup.InstallDir}，之後都從那裡執行，設定檔也在那裡 (只有系統管理員能讀寫)\n\n" +
                          "目前的連線會中斷。要安裝嗎？"
                        : "移除系統服務後回到一般模式：UAC 確認視窗、鎖定畫面、登入畫面看不到也不能操作。\n\n目前的連線會中斷。要移除嗎？";
                    if (Win32.Message(ask, Title, Win32.MB_YESNO | Win32.MB_ICONQUESTION) != Win32.IDYES) return;

                    Post(() => ShowTip(install ? "安裝系統服務中…" : "移除系統服務中…"));
                    try
                    {
                        if (install)
                        {
                            engine.Stop();
                            ServiceSetup.Install(AppContext.BaseDirectory);
                        }
                        else ServiceSetup.Uninstall();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"{(install ? "安裝" : "移除")}系統服務失敗: {ex.Message}");
                        Win32.Message($"{(install ? "安裝" : "移除")}系統服務失敗：\n\n{ex.Message}", Title, Win32.MB_ICONERROR);
                        if (!install) { Post(UpdateTip); return; }
                        // 安裝失敗：連線已經停了，重新開一次回到原本的樣子
                    }

                    // 安裝好：從 Program Files 開 (服務模式)；移除 / 安裝失敗：重新開這一份。
                    // 先跳完提示再開新的 (新的會等這個結束，最多等 30 秒)
                    bool installed = install && ServiceSetup.Installed;
                    if (installed && !ServiceSetup.IsInstalledCopy(Environment.ProcessPath))
                        Win32.Message($"已安裝成系統服務。\n\n程式已複製到 {ServiceSetup.InstallDir}，之後都從那裡執行；原本的資料夾 {AppContext.BaseDirectory} 可以刪掉。",
                                      Title, Win32.MB_ICONINFORMATION);
                    string exe = installed ? ServiceSetup.InstalledExe : Environment.ProcessPath;
                    var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
                    info.ArgumentList.Add(AutoUpdater.AfterUpdateArg);
                    Process.Start(info);
                    Post(Exit);
                }
                finally { Interlocked.Exchange(ref serviceBusy, 0); }
            }) { IsBackground = true, Name = "ServiceSetup" }.Start();
        }

        // 重新啟動遠端服務 (改了設定檔之後套用；房間碼 / 密碼沒填的話會換新的)
        void RestartService()
        {
            if (Interlocked.Exchange(ref serviceBusy, 1) == 1) return;
            ShowTip("重新啟動遠端服務中…");
            new Thread(() =>
            {
                try
                {
                    ServiceSetup.Restart();
                    Post(() => ShowTip("遠端服務已重新啟動"));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"重新啟動系統服務失敗: {ex.Message}");
                    Win32.Message($"重新啟動遠端服務失敗：\n\n{ex.Message}", "SmartRemote 系統服務", Win32.MB_ICONERROR);
                    Post(UpdateTip);
                }
                finally { Interlocked.Exchange(ref serviceBusy, 0); }
            }) { IsBackground = true, Name = "ServiceRestart" }.Start();
        }

        // 開機自動啟動：建立 / 刪掉工作排程 (最高權限) 並寫回設定檔；結果顯示在圖示的提示文字 (不跳通知)
        void ToggleAutoStart()
        {
            bool wanted = !autoStart;
            ShowTip(wanted ? "設定開機自動啟動中…" : "取消開機自動啟動中…");
            new Thread(() =>
            {
                bool ok = wanted ? AutoStart.Enable() : AutoStart.Disable();
                Post(() =>
                {
                    if (!ok)
                    {
                        ShowTip(wanted ? "開機自動啟動設定失敗，請看日誌資料夾" : "取消開機自動啟動失敗，請看日誌資料夾");
                        return;
                    }
                    autoStart = wanted;
                    try { Settings.SaveAutoStart(wanted); }
                    catch (Exception ex) { Logger.Warn($"設定檔存不了 autoStart：{ex.Message}"); }
                    ShowTip(wanted ? "已設定開機自動啟動" : "已取消開機自動啟動");
                });
            }) { IsBackground = true, Name = "AutoStart" }.Start();
        }

        // 圖示的提示文字顯示指定的字 (例如檢查更新的結果)；下一次狀態改變時會回到連線狀態
        void ShowTip(string note)
        {
            var nid = Notify(NIF_TIP);
            SetTip(ref nid, note.Length > 127 ? note[..127] : note);
            Shell_NotifyIconW(NIM_MODIFY, &nid);
        }

        // 狀態改變時更新滑鼠停在圖示上面看到的文字
        void UpdateTip()
        {
            var nid = Notify(NIF_TIP);
            string text = $"SmartRemote{(engine.IsService ? " (系統服務)" : "")}：{engine.Status}";
            SetTip(ref nid, text.Length > 127 ? text[..127] : text);
            Shell_NotifyIconW(NIM_MODIFY, &nid);
        }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (engine.IsService) engine.Stop(); // 服務模式只是關掉圖示，遠端服務繼續執行
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

        // 服務模式的設定檔只有系統管理員能改：用這個程式 (系統管理員權限) 開記事本，不然存不了檔
        void OpenSettings()
        {
            if (!engine.IsService) { OpenPath(Settings.FilePath); return; }
            try { Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false, ArgumentList = { Settings.FilePath } }); }
            catch (Exception ex) { Logger.Warn($"無法開啟設定檔: {ex.Message}"); }
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
