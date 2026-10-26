using SmartRemote.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartRemote.Tray
{
    /// <summary>
    /// 系統匣介面：右下角圖示，點右鍵看房間碼與密碼、複製網址、開關遙控、中斷連線。
    /// 連線成功 / 中斷時跳出通知；Ctrl+F7 全域快捷鍵 = 中斷所有連線並暫停接受新連線 (再按一次恢復)
    /// </summary>
    sealed class TrayApp : ApplicationContext
    {
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 1;
        const uint MOD_CONTROL = 0x0002;
        const uint VK_F7 = 0x76;

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem codeItem, controlItem, pauseItem;
        readonly HotkeyWindow window;
        readonly string room, password;

        public TrayApp(Settings settings)
        {
            room = settings.Room != "" ? settings.Room : RemoteView.RandomCode(8);
            password = settings.Password != "" ? settings.Password : RemoteView.RandomCode(10);

            codeItem = new ToolStripMenuItem($"房間碼 {room}　密碼 {password}") { Enabled = false };
            controlItem = new ToolStripMenuItem("允許遙控", null, (_, _) => ToggleControl());
            pauseItem = new ToolStripMenuItem("中斷所有連線並暫停接受 (Ctrl+F7)", null, (_, _) => TogglePause());

            var menu = new ContextMenuStrip();
            menu.Items.Add(codeItem);
            menu.Items.Add(new ToolStripMenuItem("複製房間碼與密碼", null, (_, _) => CopyCode()));
            menu.Items.Add(new ToolStripMenuItem("複製觀看網址 (含房間碼與密碼)", null, (_, _) => CopyUrl()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(controlItem);
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("開啟連線紀錄", null, (_, _) => OpenPath(RemoteLog.FilePath)));
            menu.Items.Add(new ToolStripMenuItem("開啟設定檔", null, (_, _) => OpenPath(Settings.FilePath)));
            menu.Items.Add(new ToolStripMenuItem("開啟日誌資料夾", null, (_, _) => OpenPath(AppPaths.InBaseDir("logs"))));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("結束", null, (_, _) => ExitThread()));

            tray = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "SmartRemote",
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.DoubleClick += (_, _) => CopyUrl();

            window = new HotkeyWindow(() => TogglePause());
            if (!Win32.RegisterHotKey(window.Handle, HotkeyId, MOD_CONTROL, VK_F7))
                Logger.Warn($"Ctrl+F7 快捷鍵註冊失敗 (Windows 錯誤碼 {Marshal.GetLastWin32Error()}，1409 = 已被其他程式佔用)");

            RemoteLog.Entry += OnLogEntry;
            RemoteView.StateChanged += () => window.Post(Refresh);
            Refresh();

            // SMARTREMOTE_DRYRUN=1 只給測試用：遙控的輸入與剪貼簿只寫日誌，不真的動滑鼠鍵盤
            RemoteView.Start(room, password, settings.Control, Environment.GetEnvironmentVariable("SMARTREMOTE_DRYRUN") == "1");
            Logger.Info($"SmartRemote 啟動：房間碼 {room}，{(settings.Control ? "可遙控" : "只能看")}");
            if (settings.Room == "" || settings.Password == "")
                tray.ShowBalloonTip(5000, "SmartRemote 已開始等待連線", $"房間碼 {room}　密碼 {password}\n右鍵圖示可以複製網址", ToolTipIcon.Info);
            else
                tray.ShowBalloonTip(3000, "SmartRemote 已開始等待連線", "右鍵圖示可以看房間碼、複製網址", ToolTipIcon.Info);
        }

        // 連線成功 / 中斷連線 才跳通知 (其他紀錄只寫檔)
        void OnLogEntry(string line)
        {
            bool connected = line.Contains("連線成功");
            bool closed = line.Contains("中斷連線") || line.Contains("沒有連上");
            if (!connected && !closed) return;
            string text = line[line.IndexOf("觀看端", StringComparison.Ordinal)..];
            window.Post(() => tray.ShowBalloonTip(3000, connected ? "有人連線" : "連線中斷", text, ToolTipIcon.Info));
        }

        void ToggleControl()
        {
            RemoteView.SetControl(!RemoteView.AllowControl);
            tray.ShowBalloonTip(2500, "遙控", RemoteView.AllowControl ? "已開啟，之後的連線可以遙控這台電腦" : "已關閉，已連線的人也只能看", ToolTipIcon.Info);
        }

        void TogglePause()
        {
            bool paused = RemoteView.TogglePause();
            tray.ShowBalloonTip(2500, "SmartRemote", paused ? "已中斷所有連線，暫停接受新連線 (Ctrl+F7 恢復)" : "恢復接受連線", ToolTipIcon.Info);
        }

        void Refresh()
        {
            controlItem.Checked = RemoteView.AllowControl;
            pauseItem.Checked = RemoteView.Paused;
            string text = $"SmartRemote：{RemoteView.Status}";
            tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon.Text 上限 63 字
        }

        void CopyCode()
        {
            Clipboard.SetText($"房間碼：{room}\n密碼：{password}");
            tray.ShowBalloonTip(2000, "SmartRemote", "已複製房間碼與密碼", ToolTipIcon.Info);
        }

        void CopyUrl()
        {
            Clipboard.SetText($"{RemoteView.ViewerUrl}#{room}/{password}");
            tray.ShowBalloonTip(2000, "SmartRemote", "已複製觀看網址", ToolTipIcon.Info);
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

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            Win32.UnregisterHotKey(window.Handle, HotkeyId);
            window.DestroyHandle();
            tray.Dispose();
            base.ExitThreadCore();
        }

        // 隱藏視窗：接收 Ctrl+F7 的 WM_HOTKEY，也當成介面執行緒的入口 (背景執行緒用 Post 丟回來)
        sealed class HotkeyWindow : NativeWindow
        {
            const uint WM_APP_INVOKE = 0x8001;
            readonly Action onHotkey;

            public HotkeyWindow(Action onHotkey)
            {
                this.onHotkey = onHotkey;
                CreateHandle(new CreateParams());
            }

            // 從任何執行緒丟一個動作回到這條執行緒執行
            public void Post(Action action)
            {
                if (Handle == IntPtr.Zero) return;
                var handle = GCHandle.Alloc(action);
                if (!PostMessage(Handle, WM_APP_INVOKE, IntPtr.Zero, GCHandle.ToIntPtr(handle))) handle.Free();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY) onHotkey();
                else if (m.Msg == WM_APP_INVOKE)
                {
                    var handle = GCHandle.FromIntPtr(m.LParam);
                    try { ((Action)handle.Target)(); }
                    catch (Exception ex) { Logger.Warn($"介面更新失敗: {ex.Message}"); }
                    finally { handle.Free(); }
                    return;
                }
                base.WndProc(ref m);
            }

            [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        }
    }
}
