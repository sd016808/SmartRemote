
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>
    /// 把觀看端傳來的滑鼠 / 鍵盤送進這台電腦 (SendInput，跟真的有人在操作一樣，任何程式都收得到)。
    /// 每個觀看端一條執行緒依序處理；記住哪些鍵 / 滑鼠鍵還按著，斷線時全部放開，不會卡鍵。
    /// dryRun (--test-remote 的 dry)：只寫日誌不真的送出
    /// </summary>
    sealed class RemoteInput : IDisposable
    {
        readonly BlockingCollection<Action> queue = new();
        readonly HashSet<KeyDef> downKeys = new();
        readonly HashSet<int> downButtons = new();
        readonly bool dryRun;
        readonly string id;

        /// <summary>有沒有真的操作過 (連線紀錄用)</summary>
        public bool Used { get; private set; }

        public RemoteInput(string id, bool dryRun)
        {
            this.id = id;
            this.dryRun = dryRun;
            var thread = new Thread(Loop) { IsBackground = true, Name = "RemoteInput-" + id };
            thread.Start();
        }

        void Loop()
        {
            Native.UsePerMonitorDpi(); // 座標用實際像素 (跟截圖一致)
            foreach (var action in queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { Logger.Warn($"[遠端畫面] 觀看端 {id} 輸入處理失敗: {ex.Message}"); }
            }
        }

        void Post(Action action)
        {
            if (!queue.IsAddingCompleted) queue.Add(action);
        }

        // ===== 滑鼠 =====
        const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040,
                   XDOWN = 0x0080, XUP = 0x0100, WHEEL = 0x0800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;

        /// <summary>移到螢幕 bounds 裡的 (x, y)，x / y 是 0~1 的比例</summary>
        public void Move(OpenCvSharp.Rect bounds, double x, double y) => Post(() => MoveNow(bounds, x, y));

        /// <summary>按下 / 放開滑鼠鍵 (b 跟瀏覽器一樣：0 左、1 中、2 右、3 上一頁、4 下一頁)，先移到 (x, y)</summary>
        public void Button(OpenCvSharp.Rect bounds, double x, double y, int b, bool down) => Post(() =>
        {
            MoveNow(bounds, x, y);
            ButtonNow(b, down);
        });

        /// <summary>滾輪：dy 正數 = 往上捲，120 = 一格</summary>
        public void Wheel(int dx, int dy) => Post(() =>
        {
            Used = true;
            if (dy != 0) SendMouse(WHEEL, 0, 0, dy);
            if (dx != 0) SendMouse(HWHEEL, 0, 0, dx);
        });

        void MoveNow(OpenCvSharp.Rect bounds, double x, double y)
        {
            Used = true;
            int px = bounds.X + (int)Math.Clamp(Math.Round(x * bounds.Width), 0, bounds.Width - 1);
            int py = bounds.Y + (int)Math.Clamp(Math.Round(y * bounds.Height), 0, bounds.Height - 1);
            // 絕對座標是整個虛擬桌面 (所有螢幕) 換算成 0~65535
            int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77), vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
            int nx = (int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1)), ny = (int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1));
            SendMouse(MOVE | ABSOLUTE | VIRTUALDESK, nx, ny, 0, $"移到 ({px}, {py})");
        }

        void ButtonNow(int b, bool down)
        {
            if (b is < 0 or > 4) return;
            if (down ? !downButtons.Add(b) : !downButtons.Remove(b)) return; // 重複按下 / 沒按下就放開：不送
            (uint flags, int data) = b switch
            {
                0 => (down ? LEFTDOWN : LEFTUP, 0),
                1 => (down ? MIDDLEDOWN : MIDDLEUP, 0),
                2 => (down ? RIGHTDOWN : RIGHTUP, 0),
                _ => (down ? XDOWN : XUP, b == 3 ? 1 : 2), // XBUTTON1 / XBUTTON2
            };
            SendMouse(flags, 0, 0, data, $"滑鼠 {b} {(down ? "按下" : "放開")}");
        }

        void SendMouse(uint flags, int dx, int dy, int data, string what = null)
        {
            if (dryRun) { Logger.Info($"[遠端畫面][測試] {what ?? $"滑鼠 0x{flags:X} {data}"}"); return; }
            var input = new Win32.INPUT { type = Win32.INPUT_MOUSE, U = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } } };
            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        // ===== 鍵盤 =====
        const uint KEYUP = 0x0002, EXTENDED = 0x0001, UNICODE = 0x0004, SCANCODE = 0x0008;

        /// <summary>按下 / 放開一個鍵 (code 是瀏覽器的 KeyboardEvent.code，例如 KeyA、ControlLeft)</summary>
        public void Key(string code, bool down) => Post(() => KeyNow(code, down));

        /// <summary>組合鍵：依序按下，再反過來放開 (例如 ControlLeft + KeyC)</summary>
        public void Keys(IReadOnlyList<string> codes) => Post(() =>
        {
            foreach (string c in codes) KeyNow(c, true);
            for (int i = codes.Count - 1; i >= 0; i--) KeyNow(codes[i], false);
        });

        /// <summary>直接打一段文字 (手機輸入法、中文)：每個字用 Unicode 送出，換行 = Enter、Tab = Tab</summary>
        public void Text(string text) => Post(() =>
        {
            Used = true;
            if (dryRun) { Logger.Info($"[遠端畫面][測試] 輸入文字 ({text.Length} 字)"); return; }
            foreach (char ch in text)
            {
                if (ch == '\r') continue;
                if (ch == '\n' || ch == '\t')
                {
                    string code = ch == '\n' ? "Enter" : "Tab";
                    KeyNow(code, true);
                    KeyNow(code, false);
                    continue;
                }
                SendKey(new Win32.KEYBDINPUT { wScan = ch, dwFlags = UNICODE });
                SendKey(new Win32.KEYBDINPUT { wScan = ch, dwFlags = UNICODE | KEYUP });
            }
        });

        /// <summary>放開所有還按著的鍵與滑鼠鍵 (觀看端切走 / 斷線)</summary>
        public void ReleaseAll() => Post(ReleaseNow);

        void ReleaseNow()
        {
            foreach (var k in downKeys.ToList()) SendKeyDef(k, false);
            downKeys.Clear();
            foreach (int b in downButtons.ToList()) ButtonNow(b, false);
        }

        void KeyNow(string code, bool down)
        {
            if (code == null || !KeyMap.TryGetValue(code, out KeyDef k)) return;
            Used = true;
            if (down) downKeys.Add(k);   // 按著不放時瀏覽器會一直送 keydown (自動重複)，照送
            else if (!downKeys.Remove(k)) return;
            SendKeyDef(k, down);
        }

        void SendKeyDef(KeyDef k, bool down)
        {
            if (dryRun) { Logger.Info($"[遠端畫面][測試] 按鍵 {k.Code} {(down ? "按下" : "放開")}"); return; }
            SendKey(k.Vk != 0
                ? new Win32.KEYBDINPUT { wVk = k.Vk, dwFlags = (k.Extended ? EXTENDED : 0) | (down ? 0 : KEYUP) }
                : new Win32.KEYBDINPUT { wScan = k.Scan, dwFlags = SCANCODE | (k.Extended ? EXTENDED : 0) | (down ? 0 : KEYUP) });
        }

        static void SendKey(Win32.KEYBDINPUT ki)
        {
            var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, U = new Win32.InputUnion { ki = ki } };
            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        // 瀏覽器 KeyboardEvent.code → 掃描碼 (遊戲比較認得掃描碼)；少數沒有固定掃描碼的鍵用虛擬鍵碼
        readonly record struct KeyDef(string Code, ushort Scan, bool Extended, ushort Vk);

        static readonly Dictionary<string, KeyDef> KeyMap = BuildKeyMap();

        static Dictionary<string, KeyDef> BuildKeyMap()
        {
            var map = new Dictionary<string, KeyDef>();
            void S(string code, ushort scan, bool ext = false) => map[code] = new KeyDef(code, scan, ext, 0);
            void V(string code, ushort vk, bool ext = false) => map[code] = new KeyDef(code, 0, ext, vk);

            const string letters = "QWERTYUIOPASDFGHJKLZXCVBNM";
            ushort[] letterScans = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32 };
            for (int i = 0; i < letters.Length; i++) S("Key" + letters[i], letterScans[i]);
            for (int d = 1; d <= 9; d++) S("Digit" + d, (ushort)(0x01 + d));
            S("Digit0", 0x0B);
            for (int f = 1; f <= 10; f++) S("F" + f, (ushort)(0x3A + f));
            S("F11", 0x57); S("F12", 0x58);
            S("Escape", 0x01); S("Minus", 0x0C); S("Equal", 0x0D); S("Backspace", 0x0E); S("Tab", 0x0F);
            S("BracketLeft", 0x1A); S("BracketRight", 0x1B); S("Enter", 0x1C); S("ControlLeft", 0x1D);
            S("Semicolon", 0x27); S("Quote", 0x28); S("Backquote", 0x29); S("ShiftLeft", 0x2A); S("Backslash", 0x2B);
            S("Comma", 0x33); S("Period", 0x34); S("Slash", 0x35); S("ShiftRight", 0x36); S("NumpadMultiply", 0x37);
            S("AltLeft", 0x38); S("Space", 0x39); S("CapsLock", 0x3A); S("NumLock", 0x45, true); S("ScrollLock", 0x46);
            S("Numpad7", 0x47); S("Numpad8", 0x48); S("Numpad9", 0x49); S("NumpadSubtract", 0x4A);
            S("Numpad4", 0x4B); S("Numpad5", 0x4C); S("Numpad6", 0x4D); S("NumpadAdd", 0x4E);
            S("Numpad1", 0x4F); S("Numpad2", 0x50); S("Numpad3", 0x51); S("Numpad0", 0x52); S("NumpadDecimal", 0x53);
            S("IntlBackslash", 0x56); S("IntlRo", 0x73); S("IntlYen", 0x7D); S("Convert", 0x79); S("NonConvert", 0x7B); S("KanaMode", 0x70);
            // 延伸鍵 (E0 開頭)
            S("NumpadEnter", 0x1C, true); S("ControlRight", 0x1D, true); S("NumpadDivide", 0x35, true); S("PrintScreen", 0x37, true);
            S("AltRight", 0x38, true); S("Home", 0x47, true); S("ArrowUp", 0x48, true); S("PageUp", 0x49, true);
            S("ArrowLeft", 0x4B, true); S("ArrowRight", 0x4D, true); S("End", 0x4F, true); S("ArrowDown", 0x50, true);
            S("PageDown", 0x51, true); S("Insert", 0x52, true); S("Delete", 0x53, true);
            S("MetaLeft", 0x5B, true); S("MetaRight", 0x5C, true); S("ContextMenu", 0x5D, true);
            // 沒有單一掃描碼的鍵
            V("Pause", 0x13);
            V("AudioVolumeMute", 0xAD, true); V("AudioVolumeDown", 0xAE, true); V("AudioVolumeUp", 0xAF, true);
            V("MediaTrackNext", 0xB0, true); V("MediaTrackPrevious", 0xB1, true); V("MediaStop", 0xB2, true); V("MediaPlayPause", 0xB3, true);
            return map;
        }

        public static bool IsKnownKey(string code) => code != null && KeyMap.ContainsKey(code);

        public void Dispose()
        {
            if (queue.IsAddingCompleted) return;
            Post(ReleaseNow);
            queue.CompleteAdding();
        }
    }
}
