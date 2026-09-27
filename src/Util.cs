using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SongSentry
{
    static class TextNorm
    {
        /// Compares song / artist names across services: "I Knew You Were Trouble." == "i knew you were trouble"
        /// (case, punctuation and extra spaces ignored).
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(c); space = false; }
                else space = true;
            }
            return sb.ToString();
        }
    }

    static class SongMatchText
    {
        static readonly System.Text.RegularExpressions.Regex brackets = new System.Text.RegularExpressions.Regex(@"\s*[\(\[][^\)\]]*[\)\]]");
        static readonly System.Text.RegularExpressions.Regex feat = new System.Text.RegularExpressions.Regex(@"\s+(feat\.?|ft\.?|featuring|with)\s+.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// Title without "(feat. X)", "[Radio Edit]", " - Remastered 2011" etc., so the same song matches across
        /// players and playlists (Pear's queue vs Windows' Now Playing, Spotify exports...).
        public static string Title(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            string s = brackets.Replace(t, "");
            int d = s.IndexOf(" - ", StringComparison.Ordinal);
            if (d > 0) s = s.Substring(0, d);
            s = feat.Replace(s, "");
            return TextNorm.Norm(s);
        }

        /// First credited artist ("David Guetta & Bebe Rexha" -> "david guetta").
        public static string FirstArtist(string a)
        {
            if (string.IsNullOrEmpty(a)) return "";
            string s = a.Split(new[] { ",", "&", " x ", " X ", " feat", " ft.", " with ", ";" }, StringSplitOptions.RemoveEmptyEntries)[0];
            return TextNorm.Norm(s);
        }

        /// Same song? Title compared loosely, artist fully or by first credited artist.
        public static bool Same(string artistA, string titleA, string artistB, string titleB)
        {
            if (Title(titleA) != Title(titleB) || Title(titleA).Length == 0) return false;
            if (string.IsNullOrWhiteSpace(artistA) || string.IsNullOrWhiteSpace(artistB)) return true;   // "Title" only entries
            return TextNorm.Norm(artistA) == TextNorm.Norm(artistB) || FirstArtist(artistA) == FirstArtist(artistB);
        }
    }

    static class Paths
    {
        public static string Data = Path.Combine(   // LogicTest points this at a temp folder
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SongSentry");

        public static string File(string name)
        {
            Directory.CreateDirectory(Data);
            return Path.Combine(Data, name);
        }
    }

    static class Json
    {
        static readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static string Write(object o) { return ser.Serialize(o); }

        public static Dictionary<string, object> Read(string s) { return ser.Deserialize<Dictionary<string, object>>(s); }

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        public static IEnumerable<Dictionary<string, object>> Objs(object o)
        {
            var list = o as ArrayList;
            if (list == null) yield break;
            foreach (object x in list)
            {
                var d = x as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool ? (bool)v : def;
        }

        public static double Num(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        public static Dictionary<string, object> Make(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    /// Small rolling debug log in %LocalAppData%\SongSentry\log.txt. Secrets (URLs with query strings, tokens,
    /// passwords) are redacted, because OBS source settings can contain API keys.
    static class Log
    {
        static readonly object gate = new object();
        static readonly Regex query = new Regex(@"(https?://[^\s""?]+)\?[^\s""]*", RegexOptions.Compiled);
        static readonly Regex secret = new Regex(@"(""?(password|token|secret|key|authentication)""?\s*[:=]\s*)""?[^"",\s}]+""?",
                                                 RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = query.Replace(s, "$1?<redacted>");
            return secret.Replace(s, "$1<redacted>");
        }

        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    string path = Paths.File("log.txt");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = Paths.File("log.old.txt");
                        if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                        System.IO.File.Move(path, old);
                    }
                    System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + Redact(msg) + Environment.NewLine,
                                                 new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    /// Normalises app identities so OBS's "Spotify.exe", GSMTC's "Spotify.exe" / "SpotifyAB.SpotifyMusic_...!Spotify",
    /// "Chrome", "MSEdge" all compare equal to a short key like "spotify", "chrome", "msedge".
    static class AppKey
    {
        public static string Of(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string s = id.Trim();
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
            if (slash >= 0) s = s.Substring(slash + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            s = s.ToLowerInvariant();
            if (s == "app" && id.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0) s = "spotify";
            return s;
        }

        public static string Pretty(string key)
        {
            switch (key)
            {
                case "spotify": return "Spotify";
                case "chrome": return "Chrome";
                case "msedge": return "Edge";
                case "brave": return "Brave";
                case "firefox": return "Firefox";
                case "opera": return "Opera";
                case "vlc": return "VLC";
                case "tidal": return "TIDAL";
                case "applemusic": return "Apple Music";
                case "deezer": return "Deezer";
                case "youtube music": return "YouTube Music";
                case "pretzel": return "Pretzel";
                case "amazon music": return "Amazon Music";
                case "": return "Nothing";
            }
            return key.Length > 0 ? char.ToUpperInvariant(key[0]) + key.Substring(1) : key;
        }

        /// Browsers report video titles, so a song there is only confirmed through MusicBrainz.
        public static bool IsBrowser(string key)
        {
            return key == "chrome" || key == "msedge" || key == "brave" || key == "firefox" || key == "opera" || key == "vivaldi"
                || key.Length == 16;   // Firefox registers a hashed AUMID
        }
    }
}
