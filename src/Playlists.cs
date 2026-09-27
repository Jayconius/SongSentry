using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace SongSentry
{
    /// "Scan playlist": works with any player that reports to Windows' media controls (Spotify, Pear / YouTube Music,
    /// browsers...). The streamer starts their stream-safe playlist; SongSentry reads the current song, marks it safe,
    /// presses "next", and repeats until the playlist loops, stalls, or the limit is reached. No accounts or API keys.
    /// Songs skipped within a few seconds don't count as plays on Spotify.
    public sealed class PlaylistScanner
    {
        public string App { get; private set; }
        public int Found { get; private set; }
        public bool Running { get; private set; }
        public string Status { get; private set; }
        public event Action Changed;

        readonly Func<List<MediaInfo>> snapshot;
        readonly Func<string, bool> skipNext;
        readonly Action<string> allow;            // receives "track:Artist - Title"
        volatile bool stop;
        public int MaxTracks = 500;
        public int StepMs = 1500, WaitMs = 6000;   // after "next", wait up to WaitMs for the song to change

        public PlaylistScanner(Func<List<MediaInfo>> snapshot, Func<string, bool> skipNext, Action<string> allow)
        {
            this.snapshot = snapshot; this.skipNext = skipNext; this.allow = allow;
            Status = "";
        }

        public void Start(string app)
        {
            if (Running) return;
            App = app; Found = 0; stop = false; Running = true;
            Status = "Starting…";
            new Thread(() => Run(app)) { IsBackground = true, Name = "playlist-scan" }.Start();
        }

        public void Stop() { stop = true; }

        void Raise() { var h = Changed; if (h != null) h(); }

        /// Runs synchronously (tests call this directly).
        public void Run(string app)
        {
            App = app; Running = true; stop = false;
            var seen = new HashSet<string>();
            int stalls = 0;
            try
            {
                MediaInfo cur = snapshot().FirstOrDefault(m => m.App == app);
                if (cur == null || string.IsNullOrEmpty(cur.Title)) { Status = "Start your playlist in " + AppKey.Pretty(app) + " first."; return; }
                while (!stop && Found < MaxTracks)
                {
                    string key = TextNorm.Norm(cur.Artist) + "|" + TextNorm.Norm(cur.Title);
                    if (seen.Contains(key)) { Status = "Done: the playlist looped. " + Found + " songs marked safe."; return; }
                    seen.Add(key);
                    allow("track:" + (string.IsNullOrEmpty(cur.Artist) ? "" : cur.Artist + " - ") + cur.Title);
                    Found++;
                    Status = "Scanning " + AppKey.Pretty(app) + "… " + Found + " songs: " + Engine.SongText(cur);
                    Raise();
                    if (!skipNext(cur.Aumid)) { Status = "Done: " + AppKey.Pretty(app) + " doesn't allow skipping. " + Found + " songs marked safe."; return; }
                    // wait for the player to report the next song
                    MediaInfo next = null;
                    var until = DateTime.UtcNow.AddMilliseconds(WaitMs);
                    while (!stop && DateTime.UtcNow < until)
                    {
                        Thread.Sleep(Math.Min(StepMs, 250));
                        next = snapshot().FirstOrDefault(m => m.App == app);
                        if (next != null && !string.IsNullOrEmpty(next.Title) && (TextNorm.Norm(next.Artist) + "|" + TextNorm.Norm(next.Title)) != key) break;
                        next = null;
                    }
                    if (next == null)
                    {
                        if (++stalls >= 2) { Status = "Done: the playlist ended. " + Found + " songs marked safe."; return; }
                        continue;
                    }
                    stalls = 0;
                    cur = next;
                    if (StepMs > 250) Thread.Sleep(StepMs - 250);
                }
                Status = stop ? "Stopped. " + Found + " songs marked safe." : "Stopped at " + MaxTracks + " songs.";
            }
            catch (Exception e) { Status = "Scan failed: " + e.Message; }
            finally { Running = false; Raise(); }
        }
    }

    /// Pear Desktop (the open-source YouTube Music desktop app): reads its current queue through its built-in
    /// "API Server" plugin (Plugins > API Server, default port 26538). The first time, Pear asks the streamer to allow
    /// SongSentry; the token is kept (encrypted) in settings.
    public static class PearDesktop
    {
        public const int DefaultPort = 26538;
        const string ClientId = "SongSentry";

        /// Returns "Artist - Title" for every song in the queue. Throws with a readable message on failure.
        public static List<string> Queue(int port, ref string token)
        {
            if (string.IsNullOrEmpty(token)) token = Authorize(port);
            string json;
            try { json = Http("GET", port, "/api/v1/queue", token); }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 401) { token = Authorize(port); json = Http("GET", port, "/api/v1/queue", token); }
                else throw;
            }
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            return ParseQueue(json);
        }

        static string Authorize(int port)
        {
            try
            {
                var d = Json.Read(Http("POST", port, "/auth/" + ClientId, null));
                string t = Json.Str(d, "accessToken");
                if (string.IsNullOrEmpty(t)) throw new InvalidOperationException("Pear didn't return a token");
                return t;
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 403) throw new InvalidOperationException("Pear Desktop refused access. Click Allow when Pear asks.");
                throw new InvalidOperationException("Pear Desktop's API Server isn't reachable on port " + port + ". In Pear: Plugins > API Server > enable.");
            }
        }

        /// Finds every queue entry (YouTube's playlistPanelVideoRenderer) and reads its title and artist.
        public static List<string> ParseQueue(string json)
        {
            var o = new List<string>();
            var d = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
            Walk(d, o);
            return o.Distinct().ToList();
        }

        static void Walk(object node, List<string> o)
        {
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                object r;
                if (d.TryGetValue("playlistPanelVideoRenderer", out r) && r is Dictionary<string, object>)
                {
                    var v = (Dictionary<string, object>)r;
                    string title = Runs(v, "title"), by = Runs(v, "shortBylineText");
                    if (string.IsNullOrEmpty(by)) by = Runs(v, "longBylineText");
                    if (by != null) by = by.Split('•')[0].Trim();
                    if (!string.IsNullOrEmpty(title)) o.Add((string.IsNullOrEmpty(by) ? "" : by + " - ") + title);
                    return;
                }
                foreach (var kv in d) Walk(kv.Value, o);
                return;
            }
            var a = node as object[];
            if (a != null) foreach (var x in a) Walk(x, o);
            var l = node as System.Collections.ArrayList;
            if (l != null) foreach (var x in l) Walk(x, o);
        }

        static string Runs(Dictionary<string, object> v, string key)
        {
            object t;
            if (!v.TryGetValue(key, out t)) return null;
            var td = t as Dictionary<string, object>;
            if (td == null) return null;
            object s;
            if (td.TryGetValue("simpleText", out s)) return Convert.ToString(s);
            object runs;
            if (!td.TryGetValue("runs", out runs)) return null;
            var sb = new StringBuilder();
            foreach (var run in (runs as object[]) ?? ((runs as System.Collections.ArrayList) ?? new System.Collections.ArrayList()).ToArray())
            {
                var rd = run as Dictionary<string, object>;
                object txt;
                if (rd != null && rd.TryGetValue("text", out txt)) sb.Append(txt);
            }
            return sb.ToString();
        }

        static string Http(string method, int port, string path, string token)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Method = method;
            req.Timeout = method == "POST" ? 60000 : 8000;   // POST /auth waits for the user to click Allow in Pear
            req.UserAgent = MusicBrainz.UserAgent;
            if (token != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            if (method == "POST") { req.ContentLength = 0; }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return resp.StatusCode == HttpStatusCode.NoContent ? "" : r.ReadToEnd();
        }
    }
}
