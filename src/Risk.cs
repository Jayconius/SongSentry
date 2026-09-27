using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SongSentry
{
    /// How likely a recognised song is to be claimed on stream. Nobody outside Twitch/YouTube can see their private
    /// databases, so this is an estimate from public data: who released the song (MusicBrainz label + owner chain).
    public enum RiskLevel { Unknown, Major, Independent, Safe }

    public enum RiskAction { Protect, Warn, Ignore }

    public sealed class RiskInfo
    {
        public RiskLevel Level;
        public string Label, Owner, Reason;   // e.g. "Republic Records", "Universal Music Group", "major label (UMG)"
        public DateTime Checked;
        public bool Failed;   // the lookup failed (network / MusicBrainz busy): never saved, retried later

        public static string LevelName(RiskLevel l)
        {
            switch (l)
            {
                case RiskLevel.Major: return "Major label";
                case RiskLevel.Independent: return "Independent";
                case RiskLevel.Safe: return "Stream-safe";
                default: return "Unknown label";
            }
        }
    }

    public static class RiskRater
    {
        // The three majors and their best-known labels / distributors. Short names use word boundaries ("RCA", not "Arcade").
        static readonly string[][] Majors =
        {
            new[] { "Universal Music Group", "universal music", "\\bumg\\b", "interscope", "geffen", "republic records", "island records",
                    "island def jam", "def jam", "capitol records", "capitol music", "\\bemi\\b", "polydor", "virgin records", "virgin music",
                    "decca", "motown", "verve", "mercury records", "a&m records", "blue note", "astralwerks", "big machine",
                    "caroline distribution", "caroline international", "universal records", "universal republic" },
            new[] { "Sony Music", "sony music", "columbia", "\\brca\\b", "\\bepic records\\b", "arista", "legacy recordings", "syco",
                    "ultra records", "the orchard", "ministry of sound", "kemosabe", "\\bsony\\b", "provident" },
            new[] { "Warner Music Group", "warner", "atlantic", "elektra", "parlophone", "asylum", "reprise", "\\bsire\\b", "nonesuch",
                    "rhino", "big beat", "300 entertainment", "fueled by ramen", "roadrunner", "spinnin", "\\bada\\b", "east west" },
        };

        /// "Universal Music Group" / "Sony Music" / "Warner Music Group" if the label name belongs to a major, else null.
        public static string MajorOf(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            string l = label.ToLowerInvariant();
            foreach (var m in Majors)
                for (int i = 1; i < m.Length; i++)
                {
                    string p = m[i];
                    bool hit = p.StartsWith("\\b") ? Regex.IsMatch(l, p) : l.Contains(p);
                    if (hit) return m[0];
                }
            return null;
        }

        /// Rates from a label name alone (AudD reports labels directly).
        public static RiskInfo FromLabel(string label, SafeLists lists)
        {
            var r = new RiskInfo { Label = label, Checked = DateTime.UtcNow };
            string safe = lists != null ? lists.MatchLabel(label) : null;
            string major = MajorOf(label);
            if (safe != null) { r.Level = RiskLevel.Safe; r.Reason = "on the " + safe + " list"; }
            else if (major != null) { r.Level = RiskLevel.Major; r.Owner = major; r.Reason = "major label (" + major + ")"; }
            else if (!string.IsNullOrWhiteSpace(label)) { r.Level = RiskLevel.Independent; r.Reason = "independent label"; }
            else { r.Level = RiskLevel.Unknown; r.Reason = "label unknown"; }
            return r;
        }

        /// Full check via MusicBrainz (blocking; ~4-8 requests at 1/s, call from a worker): Creative Commons license,
        /// release labels and each label's owners (up to 3 levels up).
        public static RiskInfo Lookup(string artist, string title, SafeLists lists)
        {
            var r = new RiskInfo { Checked = DateTime.UtcNow, Level = RiskLevel.Unknown, Reason = "not found on MusicBrainz" };
            string safeArtist = lists != null ? lists.MatchArtist(artist) : null;
            if (safeArtist != null) { r.Level = RiskLevel.Safe; r.Reason = "on the " + safeArtist + " list"; return r; }
            try
            {
                var recs = MusicBrainz.Recordings(artist, title, 4);
                if (recs.Count == 0) return r;
                var count = new Dictionary<string, int>();
                var names = new Dictionary<string, string>();
                for (int ri = 0; ri < recs.Count; ri++)
                {
                    string mbid = recs[ri];
                    if (ri < 2)   // Creative Commons license (checked on the best matches)
                    {
                        var rec = Json.Read(MusicBrainz.Get("https://musicbrainz.org/ws/2/recording/" + mbid + "?fmt=json&inc=url-rels"));
                        foreach (var rel in Json.Objs(rec.ContainsKey("relations") ? rec["relations"] : null))
                        {
                            var url = Json.Obj(rel.ContainsKey("url") ? rel["url"] : null);
                            if (Json.Str(rel, "type") == "license" && (Json.Str(url, "resource") ?? "").Contains("creativecommons.org"))
                            { r.Level = RiskLevel.Safe; r.Reason = "Creative Commons license"; return r; }
                        }
                    }
                    // labels of this version's releases (official releases count more than bootlegs/promos)
                    var rels = Json.Read(MusicBrainz.Get("https://musicbrainz.org/ws/2/release?fmt=json&limit=100&inc=labels&recording=" + mbid));
                    foreach (var rl in Json.Objs(rels.ContainsKey("releases") ? rels["releases"] : null))
                    {
                        int weight = Json.Str(rl, "status") == "Official" ? 3 : 1;
                        foreach (var li in Json.Objs(rl.ContainsKey("label-info") ? rl["label-info"] : null))
                        {
                            var lab = Json.Obj(li.ContainsKey("label") ? li["label"] : null);
                            string id = Json.Str(lab, "id"), name = Json.Str(lab, "name");
                            if (id == null || name == null) continue;
                            names[id] = name;
                            int c; count.TryGetValue(id, out c); count[id] = c + weight;
                        }
                    }
                    // stop early once a stream-safe or major label shows up
                    if (names.Values.Any(n => (lists != null && lists.MatchLabel(n) != null) || MajorOf(n) != null)) break;
                }
                var labels = count.OrderByDescending(kv => kv.Value).Select(kv => new KeyValuePair<string, string>(kv.Key, names[kv.Key])).ToList();
                if (labels.Count == 0) return r;
                r.Label = labels.FirstOrDefault(x => x.Value != "[no label]").Value ?? "[no label]";
                // released on a stream-safe label anywhere -> safe (e.g. an NCS release)
                foreach (var lab in labels)
                {
                    string safe = lists != null ? lists.MatchLabel(lab.Value) : null;
                    if (safe != null) { r.Level = RiskLevel.Safe; r.Label = lab.Value; r.Reason = "on the " + safe + " list"; return r; }
                }
                // any major by name (cheap), then the owner chain of the most frequent labels
                foreach (var lab in labels)
                {
                    string major = MajorOf(lab.Value);
                    if (major != null) { r.Level = RiskLevel.Major; r.Label = lab.Value; r.Owner = major; r.Reason = "major label (" + major + ")"; return r; }
                }
                foreach (var lab in labels.Where(x => x.Value != "[no label]").Take(3))
                {
                    string major = MajorOwner(lab.Key, 3);
                    if (major != null) { r.Level = RiskLevel.Major; r.Label = lab.Value; r.Owner = major; r.Reason = "major label (" + major + ")"; return r; }
                }
                bool self = labels.All(x => x.Value == "[no label]");
                r.Level = RiskLevel.Independent;
                r.Reason = self ? "self-released" : "independent label";
            }
            catch (Exception e) { Log.Write("risk lookup failed: " + e.Message); r.Reason = "lookup failed"; r.Failed = true; r.Level = RiskLevel.Unknown; }
            return r;
        }

        /// Follows "label ownership" / "imprint" relations upwards looking for a major.
        static string MajorOwner(string labelId, int depth)
        {
            if (depth <= 0) return null;
            var d = Json.Read(MusicBrainz.Get("https://musicbrainz.org/ws/2/label/" + labelId + "?fmt=json&inc=label-rels"));
            foreach (var rel in Json.Objs(d.ContainsKey("relations") ? d["relations"] : null))
            {
                string type = Json.Str(rel, "type") ?? "";
                if (Json.Str(rel, "direction") != "backward" || !(type.Contains("ownership") || type.Contains("imprint") || type.Contains("subsidiary"))) continue;
                var parent = Json.Obj(rel.ContainsKey("label") ? rel["label"] : null);
                string major = MajorOf(Json.Str(parent, "name")) ?? MajorOwner(Json.Str(parent, "id"), depth - 1);
                if (major != null) return major;
            }
            return null;
        }
    }

    /// Cache of risk results per song (normalised artist|title), stored in labels.json so each song is checked once.
    public sealed class RiskCache
    {
        readonly Dictionary<string, RiskInfo> map = new Dictionary<string, RiskInfo>();
        readonly object gate = new object();
        static string PathOf { get { return Paths.File("labels.json"); } }

        public RiskInfo Get(string key) { lock (gate) { RiskInfo r; return map.TryGetValue(key, out r) ? r : null; } }

        public void Put(string key, RiskInfo r) { lock (gate) map[key] = r; if (!r.Failed) Save(); }

        public void Clear() { lock (gate) map.Clear(); Save(); }

        public void Load()
        {
            try
            {
                if (!File.Exists(PathOf)) return;
                var d = Json.Read(File.ReadAllText(PathOf, Encoding.UTF8));
                lock (gate)
                    foreach (var kv in d)
                    {
                        var o = Json.Obj(kv.Value);
                        RiskLevel lv;
                        if (o == null || !Enum.TryParse(Json.Str(o, "level"), out lv)) continue;
                        map[kv.Key] = new RiskInfo { Level = lv, Label = Json.Str(o, "label"), Owner = Json.Str(o, "owner"), Reason = Json.Str(o, "reason"),
                                                     Checked = DateTime.FromBinary((long)Json.Num(o, "checked", 0)) };
                    }
            }
            catch (Exception e) { Log.Write("labels.json: " + e.Message); }
        }

        void Save()
        {
            try
            {
                Dictionary<string, object> d;
                lock (gate) d = map.Where(kv => !kv.Value.Failed).ToDictionary(kv => kv.Key, kv => (object)Json.Make("level", kv.Value.Level.ToString(), "label", kv.Value.Label,
                                                   "owner", kv.Value.Owner, "reason", kv.Value.Reason, "checked", kv.Value.Checked.ToBinary()));
                File.WriteAllText(PathOf, Json.Write(d), new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Write("labels.json save: " + e.Message); }
        }
    }
}
