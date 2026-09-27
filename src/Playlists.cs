using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
        public HashSet<string> Known;             // songs already in the target playlist (SafeLists.Key): skipped, and reaching them after new ones ends the scan
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
            App = app; Running = true; stop = false; Found = 0;
            var seen = new HashSet<string>();
            int stalls = 0, skipped = 0;
            try
            {
                MediaInfo cur = snapshot().FirstOrDefault(m => m.App == app);
                if (cur == null || string.IsNullOrEmpty(cur.Title)) { Status = "Start your playlist in " + AppKey.Pretty(app) + " first."; return; }
                while (!stop && Found < MaxTracks && skipped < 3000)
                {
                    string key = TextNorm.Norm(cur.Artist) + "|" + TextNorm.Norm(cur.Title);
                    if (seen.Contains(key)) { Status = "Done: the playlist looped. " + Found + " songs marked safe."; return; }
                    seen.Add(key);
                    if (Known != null && Known.Contains(key))
                    {
                        if (Found > 0) { Status = "Done: reached songs that were already saved. " + Found + " new songs marked safe."; return; }
                        skipped++;
                        Status = "Skipping songs that are already saved… (" + skipped + ")";
                    }
                    else
                    {
                        allow("track:" + (string.IsNullOrEmpty(cur.Artist) ? "" : cur.Artist + " - ") + cur.Title);
                        Found++;
                        Status = "Scanning " + AppKey.Pretty(app) + "… " + Found + " songs: " + Engine.SongText(cur);
                    }
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
            string id;
            return Queue(port, ref token, out id);
        }

        /// Also returns the YouTube playlist id the queue was started from, when Pear reports one (else null).
        public static List<string> Queue(int port, ref string token, out string playlistId)
        {
            playlistId = null;
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
            var m = Regex.Match(json, "\"playlistId\"\\s*:\\s*\"([A-Za-z0-9_-]{10,})\"");
            if (m.Success) playlistId = m.Groups[1].Value;
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

    /// What a playlist link (or songs copied from Spotify) turned into.
    public sealed class PlaylistRead
    {
        public string Name, Source, Url;
        public List<string> Tracks = new List<string>();   // "Artist - Title"
        public int Total;                                   // songs the playlist has (can be more than Tracks for Spotify links)
        public int Failed;                                  // copied songs that couldn't be read
    }

    /// Reads public playlists without accounts or keys:
    ///   Spotify playlist/album link -> its public embed page (Spotify only shares the first 100 songs there)
    ///   songs copied from the Spotify app (Ctrl+A, Ctrl+C = one track link per line) -> each track's embed page;
    ///     works for private playlists and Liked Songs too
    ///   YouTube / YouTube Music playlist link -> YouTube Music's web API, all songs (public or unlisted playlists)
    public static class PlaylistLinks
    {
        static readonly Regex spotifyList = new Regex(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?(?:embed/)?|spotify:)(playlist|album)[/:]([A-Za-z0-9]{22})", RegexOptions.IgnoreCase);
        static readonly Regex spotifyTrack = new Regex(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?(?:embed/)?|spotify:)track[/:]([A-Za-z0-9]{22})", RegexOptions.IgnoreCase);
        static readonly Regex youtubeList = new Regex(@"(?:youtube\.com|youtu\.be)/\S*?[?&]list=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";

        public static bool IsPlaylistLink(string s)
        {
            return !string.IsNullOrEmpty(s) && (spotifyList.IsMatch(s) || youtubeList.IsMatch(s));
        }

        /// Link(s) we can read? (false = something else: a list file URL or pasted "Artist - Title" lines)
        public static bool Recognizes(string text)
        {
            return IsPlaylistLink(text) || spotifyTrack.IsMatch(text ?? "");
        }

        /// Blocking; call from a worker. progress gets short status lines. Throws with a readable message.
        public static PlaylistRead Read(string text, Action<string> progress)
        {
            text = (text ?? "").Trim();
            var tracks = spotifyTrack.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            var list = spotifyList.Match(text);
            if (list.Success && tracks.Count == 0) return SpotifyList(list.Groups[1].Value.ToLowerInvariant(), list.Groups[2].Value);
            if (tracks.Count > 0) return SpotifyTracks(tracks, progress);
            var yt = youtubeList.Match(text);
            if (yt.Success) return YouTube(yt.Groups[1].Value, progress);
            throw new InvalidOperationException("That isn't a Spotify or YouTube playlist link.");
        }

        // ------------------------------------------------------------------ Spotify

        static PlaylistRead SpotifyList(string kind, string id)
        {
            var r = new PlaylistRead { Source = "Spotify", Url = "https://open.spotify.com/" + kind + "/" + id };
            string html;
            try { html = Get("https://open.spotify.com/embed/" + kind + "/" + id); }
            catch (WebException) { throw new InvalidOperationException(NotShared); }
            var e = SpotifyEntity(html);
            if (e == null) throw new InvalidOperationException(NotShared);
            r.Name = Json.Str(e, "name") ?? Json.Str(e, "title");
            var cache = TrackCache();
            foreach (var t in Json.Objs(e.ContainsKey("trackList") ? e["trackList"] : null))
            {
                Add(r, Json.Str(t, "subtitle"), Json.Str(t, "title"));
                string uri = Json.Str(t, "uri"), entry = Entry(Json.Str(t, "subtitle"), Json.Str(t, "title"));
                if (uri != null && uri.StartsWith("spotify:track:") && entry != null) lock (cache) cache[uri.Substring(14)] = entry;
            }
            SaveTrackCache();
            r.Total = r.Tracks.Count;
            if (kind == "playlist" && r.Tracks.Count >= 100)
                try
                {
                    var m = Regex.Match(Get(r.Url), "music:song_count\" content=\"(\\d+)\"");
                    if (m.Success) r.Total = Math.Max(r.Total, int.Parse(m.Groups[1].Value));
                }
                catch { }
            return r;
        }

        const string NotShared = "Spotify didn't share this playlist. Links only work for public playlists. For a private one, open it in Spotify, press Ctrl+A then Ctrl+C, and paste the songs here instead.";

        static PlaylistRead SpotifyTracks(List<string> ids, Action<string> progress)
        {
            var r = new PlaylistRead { Source = "Spotify", Total = ids.Count };
            var got = new string[ids.Count];
            var cache = TrackCache();
            lock (cache) for (int k = 0; k < ids.Count; k++) { string c; if (cache.TryGetValue(ids[k], out c)) got[k] = c; }
            var todo = Enumerable.Range(0, ids.Count).Where(k => got[k] == null).ToList();
            int next = -1, done = ids.Count - todo.Count, failed = 0;
            long pauseUntil = 0;   // shared: when Spotify says "too many requests", every worker waits
            var workers = new List<Thread>();
            Tls();
            if (progress != null && done > 0) progress("Reading copied songs… " + done + " of " + ids.Count + " (already known)");
            for (int w = 0; w < Math.Min(3, todo.Count); w++)
            {
                var th = new Thread(() =>
                {
                    int n;
                    while ((n = Interlocked.Increment(ref next)) < todo.Count)
                    {
                        int i = todo[n];
                        for (int attempt = 0; attempt < 10 && got[i] == null; attempt++)
                        {
                            long wait = Interlocked.Read(ref pauseUntil) - DateTime.UtcNow.Ticks;
                            if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait));
                            try
                            {
                                var e = SpotifyEntity(Get("https://open.spotify.com/embed/track/" + ids[i]));
                                if (e == null) break;
                                var artists = Json.Objs(e.ContainsKey("artists") ? e["artists"] : null).Select(a => Json.Str(a, "name")).Where(a => !string.IsNullOrEmpty(a));
                                got[i] = Entry(string.Join(", ", artists), Json.Str(e, "name") ?? Json.Str(e, "title"));
                                if (got[i] != null) lock (cache) cache[ids[i]] = got[i];
                            }
                            catch (WebException ex)
                            {
                                var resp = ex.Response as HttpWebResponse;
                                int code = resp != null ? (int)resp.StatusCode : 0;
                                if (code == 404 || code == 400) break;
                                int secs = 2 + attempt * 2;
                                if (code == 429)
                                {
                                    int ra;
                                    if (int.TryParse(resp.Headers["Retry-After"], out ra)) secs = ra;
                                    secs = Math.Max(3, Math.Min(60, secs));
                                    if (progress != null) progress("Spotify asked SongSentry to slow down, waiting " + secs + " s… (" + Interlocked.CompareExchange(ref done, 0, 0) + " of " + ids.Count + ")");
                                }
                                long until = DateTime.UtcNow.AddSeconds(secs).Ticks;
                                long cur;
                                while ((cur = Interlocked.Read(ref pauseUntil)) < until && Interlocked.CompareExchange(ref pauseUntil, until, cur) != cur) { }
                            }
                            catch { break; }
                        }
                        if (got[i] == null) Interlocked.Increment(ref failed);
                        int d = Interlocked.Increment(ref done);
                        if (progress != null && (d % 10 == 0 || d == ids.Count)) progress("Reading copied songs… " + d + " of " + ids.Count + ". Spotify limits the speed, so this can take a while; keep SongSentry open.");
                    }
                }) { IsBackground = true, Name = "spotify-tracks" };
                th.Start(); workers.Add(th);
            }
            foreach (var th in workers) th.Join();
            SaveTrackCache();
            foreach (string g in got) if (g != null && !r.Tracks.Contains(g)) r.Tracks.Add(g);
            r.Failed = failed;
            if (r.Tracks.Count == 0) throw new InvalidOperationException("Couldn't read the copied songs from Spotify. Check your internet connection and try again.");
            return r;
        }

        // Spotify track id -> "Artist - Title", so pasting a playlist again only looks up new songs
        static Dictionary<string, string> trackCache;

        static Dictionary<string, string> TrackCache()
        {
            if (trackCache != null) return trackCache;
            var d = new Dictionary<string, string>();
            try
            {
                string f = Paths.File("spotify-tracks.json");
                if (File.Exists(f))
                    foreach (var kv in Json.Read(File.ReadAllText(f, Encoding.UTF8)))
                        if (kv.Value is string) d[kv.Key] = (string)kv.Value;
            }
            catch (Exception e) { Log.Write("spotify track cache unreadable: " + e.Message); }
            return trackCache = d;
        }

        static void SaveTrackCache()
        {
            var c = TrackCache();
            try { string json; lock (c) json = Json.Write(c); File.WriteAllText(Paths.File("spotify-tracks.json"), json, Encoding.UTF8); }
            catch (Exception e) { Log.Write("spotify track cache not saved: " + e.Message); }
        }

        /// The "entity" object in a Spotify embed page (its __NEXT_DATA__ JSON), or null.
        public static Dictionary<string, object> SpotifyEntity(string html)
        {
            var m = Regex.Match(html ?? "", "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
            if (!m.Success) return null;
            var d = Json.Obj(Deserialize(m.Groups[1].Value));
            foreach (string k in new[] { "props", "pageProps", "state", "data", "entity" })
            {
                if (d == null || !d.ContainsKey(k)) return null;
                d = Json.Obj(d[k]);
            }
            return d;
        }

        // ------------------------------------------------------------------ YouTube / YouTube Music

        static PlaylistRead YouTube(string list, Action<string> progress)
        {
            if (list.StartsWith("RD")) throw new InvalidOperationException("That's a YouTube mix, which YouTube makes up as it plays, so it can't be saved. Save the songs into a playlist first.");
            if (list == "LM" || list == "LL" || list == "WL") throw new InvalidOperationException("Liked music and Watch later are private. Play them in Pear Desktop and use \"Read Pear's playlist\" instead.");
            string plain = list.StartsWith("VL") ? list.Substring(2) : list;
            var r = new PlaylistRead { Source = "YouTube Music", Url = "https://music.youtube.com/playlist?list=" + plain };
            var page = YouTubeBrowse(Json.Make("browseId", "VL" + plain));
            ParseYouTube(page, r);
            string token = Continuation(page);
            for (int n = 0; token != null && n < 80; n++)
            {
                if (progress != null) progress("Reading the playlist… " + r.Tracks.Count + " songs");
                page = YouTubeBrowse(Json.Make("continuation", token));
                int before = r.Tracks.Count;
                ParseYouTube(page, r);
                token = r.Tracks.Count > before ? Continuation(page) : null;
            }
            if (r.Tracks.Count == 0)
                throw new InvalidOperationException("YouTube didn't share this playlist. Private playlists can't be read from a link: make it Unlisted, or play it in Pear Desktop and use \"Read Pear's playlist\".");
            r.Total = Math.Max(r.Total, r.Tracks.Count);
            return r;
        }

        static object YouTubeBrowse(Dictionary<string, object> body)
        {
            body["context"] = Json.Make("client", Json.Make("clientName", "WEB_REMIX", "clientVersion", "1.20250901.01.00", "hl", "en"));
            var req = (HttpWebRequest)WebRequest.Create("https://music.youtube.com/youtubei/v1/browse?prettyPrint=false");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = Browser;
            req.Referer = "https://music.youtube.com/";
            req.Headers["Origin"] = "https://music.youtube.com";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 15000;
            Tls();
            byte[] b = Encoding.UTF8.GetBytes(Json.Write(body));
            using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Deserialize(rd.ReadToEnd());
        }

        /// Adds the songs (and the name / song count from the header) of one YouTube Music browse response.
        public static void ParseYouTube(object page, PlaylistRead r)
        {
            if (r.Name == null)
                foreach (string hk in new[] { "musicResponsiveHeaderRenderer", "musicDetailHeaderRenderer", "musicImmersiveHeaderRenderer" })
                {
                    var h = Find(page, hk).FirstOrDefault();
                    if (h == null) continue;
                    r.Name = Runs(h, "title");
                    var m = Regex.Match(Runs(h, "secondSubtitle") ?? "", @"([\d,.]+)\s+(tracks|songs|videos)");
                    if (m.Success) { int t; if (int.TryParse(m.Groups[1].Value.Replace(",", "").Replace(".", ""), out t)) r.Total = t; }
                    break;
                }
            foreach (var item in Find(page, "musicResponsiveListItemRenderer"))
            {
                var cols = Json.Objs(item.ContainsKey("flexColumns") ? item["flexColumns"] : null)
                               .Select(c => Json.Obj(c.ContainsKey("musicResponsiveListItemFlexColumnRenderer") ? c["musicResponsiveListItemFlexColumnRenderer"] : null))
                               .Select(c => c == null ? "" : Runs(c, "text") ?? "").ToList();
                if (cols.Count == 0 || cols[0].Length == 0) continue;
                string title = cols[0], artist = cols.Count > 1 ? cols[1].Split('\u2022')[0].Trim() : "";
                // a video titled "Artist - Song (Official Video)": keep just the song
                if (title.Contains(" - "))
                {
                    string a, t; SafeLists.Split(title, out a, out t);
                    string first = SongMatchText.FirstArtist(artist);
                    if (Json.Write(item).Contains("MUSIC_VIDEO_TYPE_UGC")) { artist = a; title = t; }   // uploaded by some channel: the title names the artist
                    else if (first.Length > 0 && TextNorm.Norm(a).Contains(first)) title = t;         // official video repeating the artist
                }
                Add(r, artist, title);
            }
        }

        static string Continuation(object page)
        {
            var c = Find(page, "continuationCommand").FirstOrDefault();
            if (c != null) return Json.Str(c, "token");
            var n = Find(page, "nextContinuationData").FirstOrDefault();
            return n != null ? Json.Str(n, "continuation") : null;
        }

        // ------------------------------------------------------------------ helpers

        static void Add(PlaylistRead r, string artist, string title)
        {
            string e = Entry(artist, title);
            if (e != null && !r.Tracks.Contains(e)) r.Tracks.Add(e);
        }

        static string Entry(string artist, string title)
        {
            title = Clean(title); artist = Clean(artist);
            if (title.Length == 0) return null;
            return artist.Length == 0 ? title : artist + " - " + title;
        }

        static string Clean(string s) { return Regex.Replace((s ?? "").Replace('\u00a0', ' '), @"\s+", " ").Trim(); }

        static object Deserialize(string json)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 400 }.DeserializeObject(json);
        }

        /// Every object stored under this key anywhere in the JSON tree (doesn't look inside matches).
        static List<Dictionary<string, object>> Find(object node, string key)
        {
            var o = new List<Dictionary<string, object>>();
            FindRec(node, key, o);
            return o;
        }

        static void FindRec(object node, string key, List<Dictionary<string, object>> o)
        {
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                foreach (var kv in d)
                {
                    if (kv.Key == key && kv.Value is Dictionary<string, object>) o.Add((Dictionary<string, object>)kv.Value);
                    else FindRec(kv.Value, key, o);
                }
                return;
            }
            var a = node as object[];
            if (a != null) foreach (var x in a) FindRec(x, key, o);
        }

        static string Runs(Dictionary<string, object> d, string key)
        {
            var t = d.ContainsKey(key) ? Json.Obj(d[key]) : null;
            if (t == null) return null;
            if (t.ContainsKey("simpleText")) return Convert.ToString(t["simpleText"]);
            var runs = t.ContainsKey("runs") ? t["runs"] as object[] : null;
            if (runs == null) return null;
            var sb = new StringBuilder();
            foreach (var run in runs) { var rd = Json.Obj(run); if (rd != null && rd.ContainsKey("text")) sb.Append(rd["text"]); }
            return sb.ToString();
        }

        static void Tls()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            if (ServicePointManager.DefaultConnectionLimit < 8) ServicePointManager.DefaultConnectionLimit = 8;   // default 2 per host serialises the copied-songs lookups
        }

        static string Get(string url)
        {
            Tls();
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";   // Spotify serves a script-only page (no song count) to full browser user agents
                wc.Headers[HttpRequestHeader.AcceptLanguage] = "en";
                return wc.DownloadString(url);
            }
        }
    }
}
