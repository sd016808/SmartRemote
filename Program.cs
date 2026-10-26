using SmartRemote.Core;
using SmartRemote.Tray;

namespace SmartRemote
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 同時只開一個 (兩個同時開會各自等待、房間碼與介面都亂)
            using var mutex = new Mutex(true, "SmartRemote-SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("SmartRemote 已經在執行中，請看右下角系統匣的圖示。", "SmartRemote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();
            Settings settings;
            try { settings = Settings.Load(); }
            catch (FormatException ex)
            {
                MessageBox.Show(ex.Message, "SmartRemote 設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Logger.Info("========== 啟動 SmartRemote ==========");
            Application.Run(new TrayApp(settings));
        }
    }
}
