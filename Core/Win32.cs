using System.Runtime.InteropServices;

namespace SmartRemote.Core
{
    /// <summary>送滑鼠 / 鍵盤 (SendInput)、全域快捷鍵、對話框用到的 Win32 定義</summary>
    internal static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ===== 對話框 (MessageBox) =====
        public const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40;
        const uint MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        public const int IDYES = 6;

        [DllImport("user32.dll", SetLastError = true)] static extern int MessageBoxW(IntPtr owner, IntPtr text, IntPtr caption, uint type);

        /// <summary>跳出對話框 (最上層、搶到前景：系統匣程式沒有視窗，不然會被別的視窗蓋住)；會等使用者按掉，回傳按的鈕 (IDYES…)</summary>
        public static int Message(string text, string caption, uint type)
        {
            IntPtr t = Marshal.StringToHGlobalUni(text), c = Marshal.StringToHGlobalUni(caption);
            try { return MessageBoxW(IntPtr.Zero, t, c, type | MB_SETFOREGROUND | MB_TOPMOST); }
            finally { Marshal.FreeHGlobal(t); Marshal.FreeHGlobal(c); }
        }

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    }
}
