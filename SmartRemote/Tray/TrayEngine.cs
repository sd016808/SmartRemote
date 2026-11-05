using SmartRemote.Core;
using SmartRemote.Service;
using System.Text.Json;

namespace SmartRemote.Tray
{
    /// <summary>
    /// 系統匣介面背後真正在連線的東西：
    /// - LocalEngine：一般模式，系統匣程式自己等觀看端 (RemoteView)
    /// - ServiceEngine：服務模式，連線的是系統服務開的遠端程式，系統匣只透過管道看狀態、下指令
    /// </summary>
    interface ITrayEngine
    {
        bool IsService { get; }
        string Room { get; }
        string Password { get; }
        string Status { get; }
        bool AllowControl { get; }
        bool Paused { get; }
        int Connections { get; }
        event Action StateChanged;
        void SetControl(bool on);
        void TogglePause();
        void Stop();
    }

    sealed class LocalEngine : ITrayEngine
    {
        public bool IsService => false;
        public string Room { get; }
        public string Password { get; }
        public string Status => RemoteView.Status;
        public bool AllowControl => RemoteView.AllowControl;
        public bool Paused => RemoteView.Paused;
        public int Connections => RemoteView.Connections;
        public event Action StateChanged { add => RemoteView.StateChanged += value; remove => RemoteView.StateChanged -= value; }

        public LocalEngine(Settings settings)
        {
            Room = settings.Room != "" ? settings.Room : RemoteView.RandomCode(8);
            Password = settings.Password != "" ? settings.Password : RemoteView.RandomCode(10);
            // SMARTREMOTE_DRYRUN=1 只給測試用：遙控的輸入與剪貼簿只寫日誌，不真的動滑鼠鍵盤
            RemoteView.Start(Room, Password, settings.Control, Environment.GetEnvironmentVariable("SMARTREMOTE_DRYRUN") == "1");
            Logger.Info($"SmartRemote 啟動：房間碼 {Room}，{(settings.Control ? "可遙控" : "只能看")}");
        }

        public void SetControl(bool on) => RemoteView.SetControl(on);
        public void TogglePause() => RemoteView.TogglePause();
        public void Stop() => RemoteView.Stop("電腦端換成系統服務模式");
    }

    sealed class ServiceEngine : ITrayEngine
    {
        readonly AgentPipe.Client client;
        volatile bool online;
        string room = "", password = "", status = "連接遠端服務中…";
        bool control, paused;
        int connections;

        public bool IsService => true;
        public string Room => room;
        public string Password => password;
        public string Status => online ? status : (ServiceSetup.Query() == ServiceSetup.State.Running ? "連接遠端服務中…" : "系統服務沒有在執行");
        public bool AllowControl => control;
        public bool Paused => paused;
        public int Connections => connections;
        public event Action StateChanged;

        public ServiceEngine()
        {
            client = new AgentPipe.Client(OnState);
            Logger.Info("SmartRemote 啟動 (服務模式：連線由系統服務負責)");
        }

        void OnState(JsonElement? state)
        {
            if (state is JsonElement m)
            {
                room = m.GetProperty("room").GetString();
                password = m.GetProperty("password").GetString();
                status = m.GetProperty("status").GetString();
                control = m.GetProperty("control").GetBoolean();
                paused = m.GetProperty("paused").GetBoolean();
                connections = m.GetProperty("connections").GetInt32();
                online = true;
            }
            else online = false;
            StateChanged?.Invoke();
        }

        public void SetControl(bool on) => client.Send(w => { w.WriteString("t", "control"); w.WriteBoolean("on", on); });
        public void TogglePause() => client.Send(w => w.WriteString("t", "pause"));
        public void Stop() => client.Dispose();
    }
}
