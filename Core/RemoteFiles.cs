
using SIPSorcery.Net;
using System.Text.Json;

namespace SmartRemote.Core
{
    /// <summary>
    /// 傳檔案 (觀看端開的 "files" DataChannel，只有允許遙控時才有)：
    /// - 瀏覽：{t:"ls",p} → {t:"dir",p,up,items:[{n,d,s,m}]}；p 沒填 = 常用資料夾 + 所有磁碟
    /// - 下載到觀看端：{t:"get",p} → {t:"fbegin",n,s} + 二進位分塊 + {t:"fend"}
    /// - 上傳到電腦：{t:"put",p (資料夾),n,s} + 二進位分塊 + {t:"pend"} → {t:"saved",p}
    /// 失敗都回 {t:"ferr",s}。一次只處理一個檔案 (觀看端排隊)
    /// </summary>
    sealed class RemoteFiles
    {
        const int Chunk = 16 * 1024;
        const ulong MaxBuffered = 1024 * 1024; // 送出還沒傳完的超過 1 MB 就等一下，不要一次塞進記憶體

        readonly RTCDataChannel dc;
        readonly string id;
        readonly Action<string> record; // 寫進連線紀錄
        FileStream upload;
        string uploadPath;
        long uploadSize;
        volatile bool closed;

        public RemoteFiles(RTCDataChannel dc, string id, Action<string> record)
        {
            this.dc = dc;
            this.id = id;
            this.record = record;
            dc.onmessage += OnMessage;
            dc.onclose += () => { closed = true; AbortUpload(); };
        }

        void OnMessage(RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data)
        {
            try
            {
                if (protocol is DataChannelPayloadProtocols.WebRTC_Binary or DataChannelPayloadProtocols.WebRTC_Binary_Partial)
                {
                    upload?.Write(data);
                    return;
                }
                using var doc = JsonDocument.Parse(data);
                var m = doc.RootElement;
                string p = m.TryGetProperty("p", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;
                switch (m.GetProperty("t").GetString())
                {
                    case "ls": List(p); break;
                    case "get": _ = Task.Run(() => SendFile(p)); break;
                    case "put": BeginUpload(p, m.GetProperty("n").GetString(), m.GetProperty("s").GetInt64()); break;
                    case "pend": EndUpload(); break;
                }
            }
            catch (Exception ex)
            {
                AbortUpload();
                Error(ex.Message);
            }
        }

        void List(string path)
        {
            dc.send(RemoteView.Json(w =>
            {
                w.WriteString("t", "dir");
                w.WriteString("p", path ?? "");
                w.WriteStartArray("items");
                if (string.IsNullOrEmpty(path))
                {
                    // 常用資料夾 + 磁碟
                    void Folder(string name, string full) { if (Directory.Exists(full)) Item(w, name, full, true, 0, null); }
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    Folder("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                    Folder("下載", Path.Combine(home, "Downloads"));
                    Folder("文件", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                    Folder("圖片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                    foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                        Item(w, $"{d.Name.TrimEnd('\\')} {d.VolumeLabel}".Trim(), d.RootDirectory.FullName, true, 0, null);
                }
                else
                {
                    var dir = new DirectoryInfo(path);
                    int n = 0;
                    foreach (var d in dir.EnumerateDirectories().Where(Visible).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                        if (n++ < 3000) Item(w, d.Name, d.FullName, true, 0, d.LastWriteTime);
                    foreach (var f in dir.EnumerateFiles().Where(Visible).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                        if (n++ < 3000) Item(w, f.Name, f.FullName, false, f.Length, f.LastWriteTime);
                }
                w.WriteEndArray();
                if (!string.IsNullOrEmpty(path)) w.WriteString("up", Directory.GetParent(path)?.FullName ?? "");
            }));
        }

        // 不顯示隱藏 / 系統檔 (例如 desktop.ini、$Recycle.Bin)
        static bool Visible(FileSystemInfo f) => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

        static void Item(Utf8JsonWriter w, string name, string full, bool isDir, long size, DateTime? modified)
        {
            w.WriteStartObject();
            w.WriteString("n", name);
            w.WriteString("p", full);
            w.WriteBoolean("d", isDir);
            if (!isDir) w.WriteNumber("s", size);
            if (modified != null) w.WriteString("m", modified.Value.ToString("yyyy-MM-dd HH:mm"));
            w.WriteEndObject();
        }

        async Task SendFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                dc.send(RemoteView.Json(w => { w.WriteString("t", "fbegin"); w.WriteString("n", Path.GetFileName(path)); w.WriteNumber("s", fs.Length); }));
                var buf = new byte[Chunk];
                int n;
                while ((n = await fs.ReadAsync(buf)) > 0)
                {
                    while (dc.bufferedAmount > MaxBuffered && !closed) await Task.Delay(10);
                    if (closed || dc.readyState != RTCDataChannelState.open) return;
                    dc.send(buf, 0, n);
                }
                dc.send(RemoteView.Json(w => w.WriteString("t", "fend")));
                Logger.Info($"[遠端畫面] 觀看端 {id} 下載了 {path} ({fs.Length} bytes)");
                record($"從電腦下載 {path} ({SizeText(fs.Length)})");
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        void BeginUpload(string folder, string name, long size)
        {
            AbortUpload();
            name = Path.GetFileName(name ?? "");
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("檔名不能用");
            if (!Directory.Exists(folder)) throw new IOException("資料夾不存在");
            // 同名就加 (1)、(2)…，不覆蓋原本的檔案
            string path = Path.Combine(folder, name);
            for (int i = 1; File.Exists(path); i++) path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
            upload = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            uploadPath = path;
            uploadSize = size;
        }

        void EndUpload()
        {
            if (upload == null) return;
            long got = upload.Length;
            upload.Dispose();
            upload = null;
            if (got != uploadSize)
            {
                TryDelete(uploadPath);
                throw new IOException($"檔案不完整 ({got}/{uploadSize} bytes)");
            }
            Logger.Info($"[遠端畫面] 觀看端 {id} 上傳了 {uploadPath} ({got} bytes)");
            record($"上傳到電腦 {uploadPath} ({SizeText(got)})");
            string saved = uploadPath;
            dc.send(RemoteView.Json(w => { w.WriteString("t", "saved"); w.WriteString("p", saved); }));
        }

        // 傳到一半斷線 / 出錯：刪掉不完整的檔案
        void AbortUpload()
        {
            if (upload == null) return;
            upload.Dispose();
            upload = null;
            TryDelete(uploadPath);
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        void Error(string message)
        {
            Logger.Info($"[遠端畫面] 觀看端 {id} 傳檔案失敗: {message}");
            try { if (dc.readyState == RTCDataChannelState.open) dc.send(RemoteView.Json(w => { w.WriteString("t", "ferr"); w.WriteString("s", message); })); }
            catch { }
        }

        public static string SizeText(long bytes) =>
            bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):F1} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
