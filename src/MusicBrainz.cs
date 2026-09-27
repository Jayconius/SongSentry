using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SongSentry
{
    public sealed class SongMatch
    {
        public bool Found;
        public string Artist, Title, Release, Mbid;
        public int Score;
    }

    /// Confirms "is this a real released song?" for media whose title is free text (browsers / YouTube).
    /// Free MusicBrainz API: max 1 request per second and a meaningful User-Agent. Results are cached.
    public static class MusicBrainz
    {
        public const string UserAgent = "SongSentry/1.0 beta ( https://github.com/Jayconius )";
        static readonly object rate = new object();
        static DateTime last = DateTime.MinValue;
        static readonly Dictionary<string, SongMatch> cache = new Dictionary<string, SongMatch>();

        static MusicBrainz()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;   // TLS 1.2 / 1.3
        }

        static readonly Regex junk = new Regex(
            @"\s*[\(\[【][^\)\]】]*(official|video|audio|lyric|visuali[sz]er|hd|hq|4k|mv|m/v|clip|remaster|full|explicit|color coded)[^\)\]】]*[\)\]】]",
            RegexOptions.IgnoreCase);
        static readonly Regex feat = new Regex(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase);

        /// Splits a YouTube-style title ("Artist - Song (Official Video)") into artist and song.
        public static void Parse(string rawTitle, string rawArtist, out string artist, out string title)
        {
            string t = junk.Replace(rawTitle ?? "", "").Trim();
            t = Regex.Replace(t, @"\s*[\(\[][^\)\]]*[\)\]]\s*$", m => m.Value.ToLowerInvariant().Contains("remix") ? m.Value : "").Trim();
            string a = (rawArtist ?? "").Trim();
            a = Regex.Replace(a, @"(VEVO|\s*-\s*Topic|\s*Official)$", "", RegexOptions.IgnoreCase).Trim();
            var parts = Regex.Split(t, @"\s+[-–—|]\s+");
            if (parts.Length >= 2)
            {
                artist = parts[0].Trim();
                title = string.Join(" - ", parts.Skip(1)).Trim();
            }
            else { artist = a; title = t; }
            title = feat.Replace(title, "").Trim().Trim('"', '\'', '“', '”');
        }

        /// Blocking lookup (call from a worker). Returns cached answers instantly.
        public static SongMatch Lookup(string artist, string title)
        {
            string key = (artist + "\n" + title).ToLowerInvariant();
            lock (cache) { SongMatch hit; if (cache.TryGetValue(key, out hit)) return hit; }
            var res = new SongMatch();
            if (!string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    string q = "recording:\"" + Esc(title) + "\"" + (string.IsNullOrWhiteSpace(artist) ? "" : " AND artist:\"" + Esc(artist) + "\"");
                    string url = "https://musicbrainz.org/ws/2/recording?fmt=json&limit=5&query=" + Uri.EscapeDataString(q);
                    var d = Json.Read(Get(url));
                    var best = Json.Objs(d.ContainsKey("recordings") ? d["recordings"] : null).FirstOrDefault();
                    if (best != null)
                    {
                        res.Score = (int)Json.Num(best, "score", 0);
                        res.Title = Json.Str(best, "title");
                        res.Mbid = Json.Str(best, "id");
                        res.Artist = string.Join(", ", Json.Objs(best.ContainsKey("artist-credit") ? best["artist-credit"] : null).Select(x => Json.Str(x, "name")));
                        var rel = Json.Objs(best.ContainsKey("releases") ? best["releases"] : null).FirstOrDefault();
                        res.Release = rel != null ? Json.Str(rel, "title") : null;
                        res.Found = res.Score >= 90;
                    }
                }
                catch (Exception e)
                {
                    Log.Write("MusicBrainz lookup failed: " + e.Message);
                    return res;   // not cached: try again next time
                }
            }
            lock (cache) cache[key] = res;
            return res;
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if ("+-&|!(){}[]^\"~*?:\\/".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Get(string url)
        {
            lock (rate)
            {
                double wait = 1100 - (DateTime.UtcNow - last).TotalMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                last = DateTime.UtcNow;
            }
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                wc.Headers[HttpRequestHeader.Accept] = "application/json";
                return wc.DownloadString(url);
            }
        }
    }
}
