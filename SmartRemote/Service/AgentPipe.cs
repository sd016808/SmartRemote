using SmartRemote.App;
using SmartRemote.Core;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SmartRemote.Service
{
    /// <summary>
    /// 遠端程式 (服務模式) ↔ 系統匣程式 的具名管道 \\.\pipe\SmartRemote-Agent，一行一則 JSON：
    /// - 遠端程式 → 系統匣：{"t":"state","room","password","status","control","paused","connections","version"}，連上時與每次狀態改變時送
    /// - 系統匣 → 遠端程式：{"t":"control","on":true}、{"t":"pause"}
    /// 只有 SYSTEM 與以系統管理員權限執行的程式能開 (管道裡有密碼，也能開關遙控)；系統匣程式確認對方是 SYSTEM 才相信。
    /// 兩端都要用非同步模式 (PipeOptions.Asynchronous)：同步模式的管道一邊在等著讀的時候，另一條執行緒寫不進去，
    /// 會把送狀態的那條執行緒卡住 (曾經卡住 WebRTC 連線好幾分鐘)。送狀態一律交給每個系統匣各自的寫入執行緒，不在呼叫端等
    /// </summary>
    static class AgentPipe
    {
        // 測試用 (SMARTREMOTE_PIPE_TEST=1)：不用 SYSTEM 也能連，名稱也不同，不會跟已經安裝的服務搶
        public static readonly bool TestMode = Environment.GetEnvironmentVariable("SMARTREMOTE_PIPE_TEST") == "1";
        static readonly string PipeName = TestMode ? "SmartRemote-Agent-Test" : "SmartRemote-Agent";

        // ===== 遠端程式這端 =====
        static string room, password;
        static readonly List<System.Collections.Concurrent.BlockingCollection<string>> clients = new();

        public static void StartServer(string room, string password)
        {
            AgentPipe.room = room;
            AgentPipe.password = password;
            // 狀態改變 (可能是 WebRTC 的執行緒在呼叫)：只排進每個系統匣的佇列，馬上回去
            RemoteView.StateChanged += () =>
            {
                string state = StateJson();
                lock (clients)
                    foreach (var q in clients)
                        if (q.Count < 20) q.TryAdd(state); // 那個系統匣卡住了就不再塞
            };
            new Thread(AcceptLoop) { IsBackground = true, Name = "AgentPipe" }.Start();
        }

        static string StateJson() => RemoteView.Json(w =>
        {
            w.WriteString("t", "state");
            w.WriteString("room", room);
            w.WriteString("password", password);
            w.WriteString("status", RemoteView.Status);
            w.WriteBoolean("control", RemoteView.AllowControl);
            w.WriteBoolean("paused", RemoteView.Paused);
            w.WriteNumber("connections", RemoteView.Connections);
            w.WriteString("version", AutoUpdater.CurrentVersion.ToString());
        });

        static void AcceptLoop()
        {
            while (true)
            {
                try
                {
                    var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                               PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
                    pipe.WaitForConnection();
                    new Thread(() => Serve(pipe)) { IsBackground = true, Name = "AgentPipeClient" }.Start();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"系統匣管道出錯: {ex.Message}");
                    Thread.Sleep(2000);
                }
            }
        }

        static PipeSecurity Security()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            if (TestMode) sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        static void Serve(NamedPipeServerStream pipe)
        {
            var queue = new System.Collections.Concurrent.BlockingCollection<string>();
            queue.Add(StateJson());
            lock (clients) clients.Add(queue);
            // 寫入執行緒：佇列裡的狀態依序送出 (對方沒在讀的話只有這條執行緒會等)
            new Thread(() =>
            {
                var writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
                try { foreach (string line in queue.GetConsumingEnumerable()) writer.WriteLine(line); }
                catch (Exception) { /* 對方關了 */ }
            }) { IsBackground = true, Name = "AgentPipeWrite" }.Start();
            try
            {
                using var reader = new StreamReader(pipe);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var m = doc.RootElement;
                        switch (m.GetProperty("t").GetString())
                        {
                            case "control": RemoteView.SetControl(m.GetProperty("on").GetBoolean()); break;
                            case "pause": RemoteView.TogglePause(); break;
                            case "state": queue.TryAdd(StateJson()); break;
                        }
                    }
                    catch (Exception ex) { Logger.Warn($"系統匣送來的指令看不懂: {ex.Message}"); }
                }
            }
            catch (IOException) { }
            finally
            {
                lock (clients) clients.Remove(queue);
                queue.CompleteAdding();
                try { pipe.Dispose(); } catch { }
            }
        }

        // ===== 系統匣這端 =====
        /// <summary>系統匣程式用：連到遠端程式，收到狀態就呼叫 onState；斷線時呼叫 onState(null) 並每 2 秒重連</summary>
        public sealed class Client : IDisposable
        {
            readonly Action<JsonElement?> onState;
            volatile NamedPipeClientStream pipe;
            volatile bool disposed;
            StreamWriter writer;

            public Client(Action<JsonElement?> onState)
            {
                this.onState = onState;
                new Thread(Loop) { IsBackground = true, Name = "AgentPipeClient" }.Start();
            }

            void Loop()
            {
                bool warned = false;
                while (!disposed)
                {
                    try
                    {
                        var p = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        p.Connect(2000);
                        if (!TestMode && !ServerIsSystem(p)) { p.Dispose(); throw new UnauthorizedAccessException("管道的另一端不是 SmartRemote 系統服務"); }
                        pipe = p;
                        lock (this) writer = new StreamWriter(p) { AutoFlush = true, NewLine = "\n" };
                        warned = false;
                        using var reader = new StreamReader(p);
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.GetProperty("t").GetString() == "state") onState(doc.RootElement.Clone());
                        }
                    }
                    catch (Exception ex) when (!disposed)
                    {
                        if (!warned) Logger.Info($"連不到遠端服務 ({ex.Message})，每 2 秒重試");
                        warned = true;
                    }
                    lock (this) writer = null;
                    try { pipe?.Dispose(); } catch { }
                    pipe = null;
                    if (disposed) break;
                    onState(null);
                    Thread.Sleep(2000);
                }
            }

            /// <summary>送指令給遠端程式 (在背景送，不讓介面等)；沒連上回傳 false</summary>
            public bool Send(Action<Utf8JsonWriter> body)
            {
                string line = RemoteView.Json(body);
                StreamWriter w;
                lock (this) w = writer;
                if (w == null) return false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { lock (w) w.WriteLine(line); }
                    catch (Exception) { /* 斷線了，Loop 會重連 */ }
                });
                return true;
            }

            public void Dispose()
            {
                disposed = true;
                try { pipe?.Dispose(); } catch { }
            }

            // 防止別的程式先開一個同名的管道冒充：確認另一端的程式是 SYSTEM
            static bool ServerIsSystem(NamedPipeClientStream p)
            {
                if (!GetNamedPipeServerProcessId(p.SafePipeHandle.DangerousGetHandle(), out uint pid)) return false;
                IntPtr process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (process == IntPtr.Zero) { Logger.Warn($"查不到管道另一端的程式 (PID {pid}，錯誤碼 {Marshal.GetLastPInvokeError()})"); return false; }
                try
                {
                    if (!OpenProcessToken(process, 0x8, out IntPtr token)) // TOKEN_QUERY
                    {
                        Logger.Warn($"查不到管道另一端的程式的帳號 (PID {pid}，錯誤碼 {Marshal.GetLastPInvokeError()})");
                        return false;
                    }
                    try
                    {
                        using var id = new WindowsIdentity(token);
                        return id.User != null && id.User.IsWellKnown(WellKnownSidType.LocalSystemSid);
                    }
                    finally { CloseHandle(token); }
                }
                finally { CloseHandle(process); }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
