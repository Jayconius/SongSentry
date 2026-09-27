using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SongSentry
{
    /// Stream-safe music lists: built-in ones (free libraries, and paid ones the streamer switches on when they have a
    /// license) plus lists imported from a file or URL. Songs by these labels/artists count as stream-safe.
    public sealed class SafeList
    {
        public string Id, Name, Note;
        public bool Paid;                 // needs a subscription/license: off by default, the streamer confirms they have it
        public bool Imported;
        public string Url;                // imported from here (refreshed on start); null for files / built-in
        public bool Playlist;             // a named playlist the streamer added (link, copied songs, Pear, scan)
        public string Source;             // "Spotify", "YouTube Music", "Pear Desktop", "Scan · Spotify", "File"...
        public DateTime Added;
        public int Total;                 // songs the source says it has (0 = unknown); above Tracks.Count when only part was readable
        public List<string> Labels = new List<string>(), Artists = new List<string>(), Tracks = new List<string>();
        public int Count { get { return Labels.Count + Artists.Count + Tracks.Count; } }
    }

    public sealed class SafeLists
    {
        public readonly List<SafeList> BuiltIn = new List<SafeList>
        {
            L("streambeats", "StreamBeats", false, "Harris Heller's free stream-safe library",
              new[] { "StreamBeats", "Streambeats by Harris Heller" }, new[] { "StreamBeats", "Harris Heller", "StreamBeats by Harris Heller" }),
            L("ncs", "NoCopyrightSounds (NCS)", false, "free for creators; credit the artist",
              new[] { "NCS", "NoCopyrightSounds" }, new string[0]),
            L("fixt", "FiXT", false, "indie label, allows streaming",
              new[] { "FiXT", "FiXT Neon", "FiXT Noir" }, new string[0]),
            L("epidemic", "Epidemic Sound", true, "turn on if you subscribe",
              new[] { "Epidemic Sound" }, new string[0]),
            L("monstercat", "Monstercat", true, "turn on if you have Gold",
              new[] { "Monstercat", "Monstercat Instinct", "Monstercat Uncaged", "Monstercat Silk" }, new string[0]),
            L("artlist", "Artlist", true, "turn on if you subscribe",
              new[] { "Artlist", "Artlist Original" }, new string[0]),
            L("soundstripe", "Soundstripe", true, "turn on if you subscribe",
              new[] { "Soundstripe" }, new string[0]),
            L("pretzel", "Pretzel", true, "turn on if you use Pretzel",
              new[] { "Pretzel", "Pretzel Rocks" }, new string[0]),
        };

        public readonly List<SafeList> Imported = new List<SafeList>();
        public readonly HashSet<string> Enabled = new HashSet<string>();   // ids of enabled lists (built-in or imported)
        readonly object gate = new object();

        static SafeList L(string id, string name, bool paid, string note, string[] labels, string[] artists)
        {
            return new SafeList { Id = id, Name = name, Paid = paid, Note = note, Labels = labels.ToList(), Artists = artists.ToList() };
        }

        public IEnumerable<SafeList> All { get { lock (gate) return BuiltIn.Concat(Imported).ToList(); } }

        IEnumerable<SafeList> Active { get { lock (gate) return BuiltIn.Concat(Imported).Where(l => Enabled.Contains(l.Id)).ToList(); } }

        /// Every list/playlist the streamer added (not the built-in ones).
        public List<SafeList> Mine { get { lock (gate) return Imported.ToList(); } }

        public SafeList ById(string id) { lock (gate) return Imported.FirstOrDefault(l => l.Id == id); }

        public SafeList ByName(string name)
        {
            string n = (name ?? "").Trim();
            lock (gate) return Imported.FirstOrDefault(l => string.Equals(l.Name, n, StringComparison.CurrentCultureIgnoreCase));
        }

        /// "My Playlist #1", "#2"... the first number not taken yet.
        public string NextAutoName(string prefix)
        {
            int n = 1;
            while (ByName(prefix + " #" + n) != null) n++;
            return prefix + " #" + n;
        }

        static string NewId() { return "import:" + Guid.NewGuid().ToString("N").Substring(0, 8); }

        /// Saves songs as a named playlist, or adds them to the one with the same link or name. Returns the playlist;
        /// added = how many songs were new.
        public SafeList SavePlaylist(string name, string source, string url, IEnumerable<string> tracks, int total, out int added)
        {
            lock (gate)
            {
                var l = (url != null ? Imported.FirstOrDefault(x => x.Url == url) : null) ?? ByName(name);
                if (l == null)
                {
                    l = new SafeList { Id = NewId(), Name = name.Trim(), Imported = true, Playlist = true, Source = source, Url = url, Added = DateTime.Now };
                    Imported.Add(l); Enabled.Add(l.Id);
                }
                if (l.Url == null) l.Url = url;
                added = AddLocked(l, tracks);
                l.Total = Math.Max(l.Total, total);
                return l;
            }
        }

        /// Adds songs ("Artist - Title") to a playlist, skipping ones it already has. Returns how many were new.
        public int AddTracks(string id, IEnumerable<string> tracks)
        {
            lock (gate) { var l = Imported.FirstOrDefault(x => x.Id == id); return l == null ? 0 : AddLocked(l, tracks); }
        }

        static int AddLocked(SafeList l, IEnumerable<string> tracks)
        {
            var have = new HashSet<string>(l.Tracks.Select(Key));
            var list = new List<string>(l.Tracks);
            int n = 0;
            foreach (string t in tracks)
                if (!string.IsNullOrWhiteSpace(t) && have.Add(Key(t))) { list.Add(t.Trim()); n++; }
            l.Tracks = list;   // a new list object, so the match index notices
            return n;
        }

        public void Rename(string id, string name) { lock (gate) { var l = Imported.FirstOrDefault(x => x.Id == id); if (l != null) l.Name = name.Trim(); } }

        /// "Artist - Title" -> artist, title (no " - " = title only).
        public static void Split(string entry, out string artist, out string title)
        {
            int d = entry.IndexOf(" - ", StringComparison.Ordinal);
            if (d > 0) { artist = entry.Substring(0, d); title = entry.Substring(d + 3); }
            else { artist = ""; title = entry; }
        }

        /// Same-song key used for de-duplicating and by the playlist scanner.
        public static string Key(string entry)
        {
            string a, t; Split(entry, out a, out t);
            return TextNorm.Norm(a) + "|" + TextNorm.Norm(t);
        }

        /// Name of the enabled list containing this label, or null. Matches "Monstercat" in "Monstercat Instinct".
        public string MatchLabel(string label)
        {
            string n = TextNorm.Norm(label);
            if (n.Length == 0) return null;
            foreach (var l in Active)
                foreach (string x in l.Labels)
                {
                    string xn = TextNorm.Norm(x);
                    if (xn.Length > 0 && (n == xn || n.StartsWith(xn + " "))) return l.Name;
                }
            return null;
        }

        public string MatchArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return null;
            var parts = artist.Split(new[] { ",", "&", " feat. ", " ft. ", " x ", ";" }, StringSplitOptions.RemoveEmptyEntries).Select(TextNorm.Norm).ToList();
            parts.Add(TextNorm.Norm(artist));
            foreach (var l in Active)
                foreach (string x in l.Artists)
                    if (parts.Contains(TextNorm.Norm(x))) return l.Name;
            return null;
        }

        /// Name of the enabled list/playlist containing this song, or null. Loose, like SongMatchText.Same. A YouTube
        /// video title "Artist - Song (Official Video)" is also tried as artist + song.
        public string MatchTrack(string artist, string title)
        {
            string hit = MatchOne(artist, title);
            if (hit == null && title != null && title.Contains(" - "))
            {
                string a, t; Split(title, out a, out t);
                hit = MatchOne(a, t);
            }
            return hit;
        }

        string MatchOne(string artist, string title)
        {
            string t = SongMatchText.Title(title);
            if (t.Length == 0) return null;
            List<KeyValuePair<string, string>> hits;
            if (!Index().TryGetValue(t, out hits)) return null;
            foreach (var h in hits)
                if (string.IsNullOrWhiteSpace(h.Key) || string.IsNullOrWhiteSpace(artist) || TextNorm.Norm(h.Key) == TextNorm.Norm(artist)
                    || SongMatchText.FirstArtist(h.Key) == SongMatchText.FirstArtist(artist)) return h.Value;
            return null;
        }

        // loose title -> (artist, list name); rebuilt when the enabled lists or their songs change
        string indexSig;
        Dictionary<string, List<KeyValuePair<string, string>>> index;

        Dictionary<string, List<KeyValuePair<string, string>>> Index()
        {
            lock (gate)
            {
                var act = BuiltIn.Concat(Imported).Where(l => Enabled.Contains(l.Id)).ToList();
                string sig = string.Join("|", act.Select(l => l.Id + ":" + l.Name + ":" + l.Tracks.Count + ":" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l.Tracks)));
                if (index != null && sig == indexSig) return index;
                var d = new Dictionary<string, List<KeyValuePair<string, string>>>();
                foreach (var l in act)
                    foreach (string e in l.Tracks)
                    {
                        string a, t; Split(e, out a, out t);
                        string k = SongMatchText.Title(t);
                        if (k.Length == 0) continue;
                        List<KeyValuePair<string, string>> v;
                        if (!d.TryGetValue(k, out v)) d[k] = v = new List<KeyValuePair<string, string>>();
                        v.Add(new KeyValuePair<string, string>(a, l.Name));
                    }
                indexSig = sig; index = d;
                return d;
            }
        }

        /// Parses a list: one entry per line: "label: X", "artist: Y", "Artist - Title", or CSV exports with
        /// "Track Name"/"Artist Name(s)" columns (Exportify, TuneMyMusic, Soundiiz...). "#" lines are comments.
        public static SafeList Parse(string text, string name)
        {
            var l = new SafeList { Id = NewId(), Name = name, Imported = true, Added = DateTime.Now };
            var lines = text.Replace("\r", "").Split('\n');
            int ti = -1, ai = -1;
            if (lines.Length > 0 && lines[0].Contains(","))
            {
                var head = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
                ti = head.FindIndex(h => h == "track name" || h == "title" || h == "name" || h == "track");
                ai = head.FindIndex(h => h.StartsWith("artist"));
            }
            foreach (string raw in lines.Skip(ti >= 0 && ai >= 0 ? 1 : 0))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                if (ti >= 0 && ai >= 0)
                {
                    var c = SplitCsv(s);
                    if (c.Count > Math.Max(ti, ai) && c[ti].Trim().Length > 0) l.Tracks.Add(c[ai].Split(',', ';')[0].Trim() + " - " + c[ti].Trim());
                    continue;
                }
                if (s.StartsWith("label:", StringComparison.OrdinalIgnoreCase)) l.Labels.Add(s.Substring(6).Trim());
                else if (s.StartsWith("artist:", StringComparison.OrdinalIgnoreCase)) l.Artists.Add(s.Substring(7).Trim());
                else if (s.Contains(" - ")) l.Tracks.Add(s);
                else l.Artists.Add(s);
            }
            l.Playlist = l.Labels.Count == 0 && l.Artists.Count == 0;   // only songs: it's a playlist
            l.Total = l.Tracks.Count;
            return l;
        }

        static List<string> SplitCsv(string line)
        {
            var o = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { if (q && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = !q; }
                else if (c == ',' && !q) { o.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            o.Add(sb.ToString());
            return o;
        }

        public SafeList ImportFile(string path)
        {
            var l = Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileNameWithoutExtension(path));
            l.Source = "File";
            Add(l);
            return l;
        }

        /// Blocking download (call from a worker). Refreshable later via RefreshUrls.
        public SafeList ImportUrl(string url)
        {
            return AddParsed(Parse(Download(url), new Uri(url).Segments.Last().Trim('/')), url);
        }

        /// Adds a list parsed elsewhere (pasted lines, a downloaded list); url = refresh it from there on start.
        public SafeList AddParsed(SafeList l, string url)
        {
            l.Url = url;
            if (l.Source == null) l.Source = url != null ? "Link" : "Pasted";
            Add(l);
            return l;
        }

        void Add(SafeList l) { lock (gate) { Imported.Add(l); Enabled.Add(l.Id); } }

        public void Remove(string id) { lock (gate) { Imported.RemoveAll(x => x.Id == id); Enabled.Remove(id); } }

        public void RefreshUrls()
        {
            foreach (var l in Imported.Where(x => x.Url != null).ToList())
                try
                {
                    if (PlaylistLinks.IsPlaylistLink(l.Url))
                    {
                        // a Spotify / YouTube playlist: add its new songs, keep the ones added by copy-paste or scanning
                        var r = PlaylistLinks.Read(l.Url, null);
                        lock (gate) { AddLocked(l, r.Tracks); l.Total = Math.Max(r.Total, l.Tracks.Count); }
                        continue;
                    }
                    var fresh = Parse(Download(l.Url), l.Name);
                    lock (gate) { l.Labels = fresh.Labels; l.Artists = fresh.Artists; l.Tracks = fresh.Tracks; }
                }
                catch (Exception e) { Log.Write("list refresh failed (" + l.Url + "): " + e.Message); }
        }

        public static string Download(string url)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.UserAgent] = MusicBrainz.UserAgent;
                return wc.DownloadString(url);
            }
        }

        // ------------------------------------------------------------------ persistence (inside settings.json)

        public object ToJson()
        {
            lock (gate)
                return Json.Make("enabled", Enabled.ToList(),
                    "imported", Imported.Select(l => Json.Make("id", l.Id, "name", l.Name, "url", l.Url, "playlist", l.Playlist, "source", l.Source,
                        "added", l.Added == DateTime.MinValue ? null : l.Added.ToString("o"), "total", l.Total,
                        "labels", l.Labels, "artists", l.Artists, "tracks", l.Tracks)).ToList());
        }

        public void FromJson(Dictionary<string, object> d)
        {
            if (d == null) { foreach (var b in BuiltIn.Where(x => !x.Paid)) Enabled.Add(b.Id); return; }   // first run: free lists on
            lock (gate)
            {
                Enabled.Clear();
                foreach (object o in (d.ContainsKey("enabled") ? d["enabled"] as System.Collections.ArrayList : null) ?? new System.Collections.ArrayList())
                    if (o is string) Enabled.Add((string)o);
                Imported.Clear();
                foreach (var x in Json.Objs(d.ContainsKey("imported") ? d["imported"] : null))
                {
                    var l = new SafeList { Id = Json.Str(x, "id"), Name = Json.Str(x, "name"), Url = Json.Str(x, "url"), Imported = true };
                    l.Labels = Strings(x, "labels"); l.Artists = Strings(x, "artists"); l.Tracks = Strings(x, "tracks");
                    l.Playlist = Json.Bool(x, "playlist", false); l.Source = Json.Str(x, "source"); l.Total = (int)Json.Num(x, "total", 0);
                    DateTime at;
                    if (DateTime.TryParse(Json.Str(x, "added"), null, System.Globalization.DateTimeStyles.RoundtripKind, out at)) l.Added = at;
                    if (l.Id != null) Imported.Add(l);
                }
                Enabled.RemoveWhere(id => !BuiltIn.Any(b => b.Id == id) && !Imported.Any(x => x.Id == id));   // e.g. a stray "import" left by 1.1.0's Import… button bug
            }
        }

        static List<string> Strings(Dictionary<string, object> d, string key)
        {
            var o = new List<string>();
            var a = d.ContainsKey(key) ? d[key] as System.Collections.ArrayList : null;
            if (a != null) foreach (object x in a) if (x is string) o.Add((string)x);
            return o;
        }
    }
}
