using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SongSentry
{
    public enum ObsState { Stopped, Connecting, Connected, AuthFailed, Unreachable }

    /// obs-websocket v5 client: connects, authenticates, reconnects on its own, raises OBS events, and runs blocking
    /// requests (call them from a worker thread, never the UI thread).
    public sealed class ObsConnection : IDisposable
    {
        const int EventsGeneral = 1, EventsInputs = 1 << 3, EventsOutputs = 1 << 6;

        readonly object gate = new object();
        readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> pending =
            new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        ClientWebSocket ws;
        Thread thread;
        volatile bool running;
        int nextId;
        string host = "127.0.0.1", password = "";
        int port = 4455;

        public ObsState State { get; private set; }
        public string StateDetail { get; private set; }
        public string ObsVersion { get; private set; }

        public event Action StateChanged;
        public event Action<string, Dictionary<string, object>> EventReceived;

        /// Screenshot/preview tool only: pretend to be connected without a real OBS.
        internal void SetPreviewState(ObsState s, string version) { State = s; ObsVersion = version; }

        public void Configure(string host, int port, string password)
        {
            lock (gate) { this.host = host; this.port = port; this.password = password ?? ""; }
            Reconnect();
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "obs", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        /// Drop the current connection (if any) and connect again right away.
        public void Reconnect()
        {
            var w = ws;
            if (w != null) try { w.Abort(); } catch { }
            wake.Set();
        }

        void SetState(ObsState s, string detail)
        {
            if (State == s && StateDetail == detail) return;
            State = s; StateDetail = detail;
            Log.Write("OBS " + s + (detail != null ? ": " + detail : ""));
            var h = StateChanged;
            if (h != null) h();
        }

        void Loop()
        {
            while (running)
            {
                string h; int p; string pw;
                lock (gate) { h = host; p = port; pw = password; }
                SetState(ObsState.Connecting, h + ":" + p);
                bool authFailed = false;
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    var cts = new CancellationTokenSource(4000);
                    ws.ConnectAsync(new Uri("ws://" + h + ":" + p), cts.Token).Wait();
                    Identify(pw, out authFailed);
                    SetState(ObsState.Connected, null);
                    ReadLoop();
                }
                catch (Exception e)
                {
                    if (!authFailed) SetState(ObsState.Unreachable, Flatten(e));
                }
                finally
                {
                    FailPending();
                    try { ws.Dispose(); } catch { }
                    ws = null;
                }
                if (authFailed) SetState(ObsState.AuthFailed, "Wrong or missing password");
                else if (State == ObsState.Connected) SetState(ObsState.Unreachable, "Connection lost");
                // Wrong password: wait for new settings instead of hammering OBS. Otherwise retry every 3 s.
                wake.WaitOne(authFailed ? Timeout.Infinite : 3000);
            }
            SetState(ObsState.Stopped, null);
        }

        void Identify(string pw, out bool authFailed)
        {
            authFailed = false;
            var hello = Receive();
            if (hello == null) throw new Exception("OBS closed the connection");
            var d = Json.Obj(hello["d"]);
            ObsVersion = Json.Str(d, "obsWebSocketVersion");
            var identify = Json.Make("rpcVersion", 1, "eventSubscriptions", EventsGeneral | EventsInputs | EventsOutputs);
            var auth = Json.Obj(d.ContainsKey("authentication") ? d["authentication"] : null);
            if (auth != null)
            {
                if (string.IsNullOrEmpty(pw)) { authFailed = true; throw new Exception("OBS needs a password"); }
                identify["authentication"] = AuthString(pw, Json.Str(auth, "salt"), Json.Str(auth, "challenge"));
            }
            Send(1, identify);
            var reply = Receive();
            if (reply == null)
            {
                authFailed = ws.CloseStatus.HasValue && (int)ws.CloseStatus.Value == 4009;
                throw new Exception("Identify rejected: " + ws.CloseStatusDescription);
            }
        }

        public static string AuthString(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        void ReadLoop()
        {
            while (running && ws.State == WebSocketState.Open)
            {
                var m = Receive();
                if (m == null) return;
                int op = (int)Json.Num(m, "op", -1);
                var d = Json.Obj(m["d"]);
                if (op == 7)
                {
                    TaskCompletionSource<Dictionary<string, object>> tcs;
                    string id = Json.Str(d, "requestId");
                    lock (pending) { if (pending.TryGetValue(id, out tcs)) pending.Remove(id); }
                    if (tcs != null) tcs.TrySetResult(d);
                }
                else if (op == 5)
                {
                    var h = EventReceived;
                    if (h != null)
                        try { h(Json.Str(d, "eventType"), Json.Obj(d.ContainsKey("eventData") ? d["eventData"] : null) ?? new Dictionary<string, object>()); }
                        catch (Exception e) { Log.Write("event handler: " + e); }
                }
            }
        }

        /// Sends a request and waits for the answer (throws if OBS rejects it or doesn't answer in 5 s).
        public Dictionary<string, object> Request(string type, params object[] kv)
        {
            var w = ws;
            if (w == null || State != ObsState.Connected) throw new InvalidOperationException("Not connected to OBS");
            string id = Interlocked.Increment(ref nextId).ToString();
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            lock (pending) pending[id] = tcs;
            var data = Json.Make(kv);
            var d = Json.Make("requestType", type, "requestId", id);
            if (data.Count > 0) d["requestData"] = data;
            Send(6, d);
            if (!tcs.Task.Wait(5000))
            {
                lock (pending) pending.Remove(id);
                throw new TimeoutException(type + ": OBS didn't answer");
            }
            var resp = tcs.Task.Result;
            if (resp == null) throw new InvalidOperationException("Disconnected from OBS");
            var status = Json.Obj(resp["requestStatus"]);
            if (!Json.Bool(status, "result", false))
                throw new ObsRequestException(type, (int)Json.Num(status, "code", 0), Json.Str(status, "comment"));
            return Json.Obj(resp.ContainsKey("responseData") ? resp["responseData"] : null) ?? new Dictionary<string, object>();
        }

        void FailPending()
        {
            lock (pending)
            {
                foreach (var t in pending.Values) t.TrySetResult(null);
                pending.Clear();
            }
        }

        void Send(int op, Dictionary<string, object> d)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(Json.Make("op", op, "d", d)));
            sendLock.Wait();
            try { ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
            finally { sendLock.Release(); }
        }

        Dictionary<string, object> Receive()
        {
            var buf = new byte[65536];
            var all = new List<byte>();
            WebSocketReceiveResult r;
            do
            {
                r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close) return null;
                for (int i = 0; i < r.Count; i++) all.Add(buf[i]);
            } while (!r.EndOfMessage);
            return Json.Read(Encoding.UTF8.GetString(all.ToArray()));
        }

        static string Flatten(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            if (e is System.Net.Sockets.SocketException) return "OBS isn't running, or its WebSocket server is off";
            return e.Message;
        }

        public void Dispose()
        {
            running = false;
            Reconnect();
            if (thread != null) thread.Join(1500);
        }
    }

    public sealed class ObsRequestException : Exception
    {
        public readonly int Code;
        public ObsRequestException(string type, int code, string comment)
            : base(type + " failed (" + code + ")" + (string.IsNullOrEmpty(comment) ? "" : ": " + comment)) { Code = code; }
    }
}
