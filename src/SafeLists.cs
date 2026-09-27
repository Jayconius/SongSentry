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
            L("fixt", "FiXT", false, "indie label that allows streaming its catalogue",
              new[] { "FiXT", "FiXT Neon", "FiXT Noir" }, new string[0]),
            L("epidemic", "Epidemic Sound", true, "only with an active Epidemic Sound subscription",
              new[] { "Epidemic Sound" }, new string[0]),
            L("monstercat", "Monstercat", true, "only with a Monstercat Gold / Creator license",
              new[] { "Monstercat", "Monstercat Instinct", "Monstercat Uncaged", "Monstercat Silk" }, new string[0]),
            L("artlist", "Artlist", true, "only with an Artlist subscription",
              new[] { "Artlist", "Artlist Original" }, new string[0]),
            L("soundstripe", "Soundstripe", true, "only with a Soundstripe subscription",
              new[] { "Soundstripe" }, new string[0]),
            L("pretzel", "Pretzel", true, "songs licensed through Pretzel (tip: also mark the Pretzel app as a safe app)",
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

        public string MatchTrack(string artist, string title)
        {
            foreach (var l in Active)
                foreach (string t in l.Tracks)
                {
                    int d = t.IndexOf(" - ", StringComparison.Ordinal);
                    if (d > 0 && SongMatchText.Same(t.Substring(0, d), t.Substring(d + 3), artist, title)) return l.Name;
                }
            return null;
        }

        /// Parses a list: one entry per line: "label: X", "artist: Y", "Artist - Title", or CSV exports with
        /// "Track Name"/"Artist Name(s)" columns (Exportify, TuneMyMusic, Soundiiz...). "#" lines are comments.
        public static SafeList Parse(string text, string name)
        {
            var l = new SafeList { Id = "import:" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = name, Imported = true };
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
            Add(l);
            return l;
        }

        /// Blocking download (call from a worker). Refreshable later via RefreshUrls.
        public SafeList ImportUrl(string url)
        {
            var l = Parse(Download(url), new Uri(url).Segments.Last().Trim('/'));
            l.Url = url;
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
                    var fresh = Parse(Download(l.Url), l.Name);
                    lock (gate) { l.Labels = fresh.Labels; l.Artists = fresh.Artists; l.Tracks = fresh.Tracks; }
                }
                catch (Exception e) { Log.Write("list refresh failed (" + l.Url + "): " + e.Message); }
        }

        static string Download(string url)
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
                    "imported", Imported.Select(l => Json.Make("id", l.Id, "name", l.Name, "url", l.Url, "labels", l.Labels, "artists", l.Artists, "tracks", l.Tracks)).ToList());
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
                    if (l.Id != null) Imported.Add(l);
                }
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
