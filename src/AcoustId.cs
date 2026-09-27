using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SongSentry
{
    /// AcoustID (acoustid.org): free, no user key needed. Recognises a song from its FIRST seconds (Chromaprint
    /// fingerprint via the bundled fpcalc.exe). AcoustID matches whole tracks, so it also needs the track's length, which
    /// we don't know from audio: we try likely lengths, most common first, and stop at the first match (tested: a guess
    /// matches when the real length is up to 7 s shorter or 16 s longer). Free for non-commercial / open-source use.
    public static class AcoustId
    {
        const string AppKey = "NvhK2DIj2M";   // SongSentry's application key (a public client key, meant to ship in the app)
        const string Api = "https://api.acoustid.org/v2/lookup";
        public const double ClipSeconds = 18;
        public const double MinScore = 0.6;   // tested: real matches 0.76-0.98

        // Guesses cover [g-15, g+6] s each -> contiguous from ~52 s to ~10 min. Ordered by how common song lengths are.
        static readonly int[] Guesses = { 205, 227, 183, 249, 161, 271, 139, 293, 315, 117, 337, 359, 95, 381, 403, 425, 73, 447, 469, 491, 513, 535, 557, 579, 601 };
        static readonly object rate = new object();
        static DateTime last = DateTime.MinValue;

        static AcoustId()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
        }

        /// Path to fpcalc (bundled inside SongSentry.exe and unpacked on first use), or null if unavailable.
        public static string FpcalcPath
        {
            get
            {
                if (fpcalcOverride != null) return fpcalcOverride;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("fpcalc.exe"))
                    {
                        if (s == null) return null;
                        string path = Paths.File("fpcalc-1.6.1.exe");
                        if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
                        {
                            string tmp = path + ".tmp";
                            using (var f = File.Create(tmp)) s.CopyTo(f);
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(tmp, path);
                        }
                        return path;
                    }
                }
                catch (Exception e) { Log.Write("fpcalc unpack failed: " + e.Message); return null; }
            }
        }
        static string fpcalcOverride;
        public static void UseFpcalc(string path) { fpcalcOverride = path; }   // tests / dev builds without the resource

        public static bool Available { get { return FpcalcPath != null; } }

        /// Blocking; call from a worker. pcm = mono 16-bit at `rate`, starting at (or just before) the song's start.
        public static AudDResult Recognize(short[] pcm, int rate)
        {
            var res = new AudDResult();
            string fp; int dur;
            try { fp = Fingerprint(pcm, rate, out dur); }
            catch (Exception e) { res.Error = "fingerprint: " + e.Message; return res; }
            if (string.IsNullOrEmpty(fp)) { res.Error = "no fingerprint"; return res; }
            foreach (int guess in Guesses)
            {
                Dictionary<string, object> d;
                try { d = Lookup(fp, guess); }
                catch (Exception e) { res.Error = e.Message; return res; }
                if (Json.Str(d, "status") != "ok") { res.Error = "AcoustID: " + Json.Str(Json.Obj(d.ContainsKey("error") ? d["error"] : null), "message"); return res; }
                res.Ok = true;
                var best = Json.Objs(d.ContainsKey("results") ? d["results"] : null).OrderByDescending(r => Json.Num(r, "score", 0)).FirstOrDefault();
                if (best == null || Json.Num(best, "score", 0) < MinScore) continue;
                // One fingerprint can link to several recordings that start the same (the original, remasters, mash-ups...).
                // The one most people submitted is almost always the original.
                var rec = Json.Objs(best.ContainsKey("recordings") ? best["recordings"] : null).Where(r => r.ContainsKey("title"))
                              .OrderByDescending(r => Json.Num(r, "sources", 0)).FirstOrDefault();
                if (rec == null) continue;   // fingerprint known but no song data: keep trying lengths
                res.Found = true;
                res.Title = Json.Str(rec, "title");
                res.Artist = string.Join(", ", Json.Objs(rec.ContainsKey("artists") ? rec["artists"] : null).Select(x => Json.Str(x, "name")));
                var rg = Json.Objs(rec.ContainsKey("releasegroups") ? rec["releasegroups"] : null).FirstOrDefault();
                res.Album = rg != null ? Json.Str(rg, "title") : null;
                res.Timecode = "00:00";   // AcoustID matches from the song's start
                res.Link = "https://musicbrainz.org/recording/" + Json.Str(rec, "id");
                return res;
            }
            return res;
        }

        static string Fingerprint(short[] pcm, int rate, out int duration)
        {
            duration = 0;
            string exe = FpcalcPath;
            if (exe == null) throw new InvalidOperationException("fpcalc not available");
            // fpcalc 1.6.1 on Windows can't read stdin, so it gets a small temp file.
            string tmp = Paths.File("clip.raw");
            var b = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, b, 0, b.Length);
            File.WriteAllBytes(tmp, b);
            try
            {
                var psi = new ProcessStartInfo(exe, "-format s16le -rate " + rate + " -channels 1 -json \"" + tmp + "\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } throw new TimeoutException("fpcalc hung"); }
                    var d = Json.Read(o);
                    duration = (int)Json.Num(d, "duration", 0);
                    return Json.Str(d, "fingerprint");
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static Dictionary<string, object> Lookup(string fp, int duration)
        {
            lock (rate)
            {
                double wait = 350 - (DateTime.UtcNow - last).TotalMilliseconds;   // AcoustID asks for at most 3 requests / s
                if (wait > 0) Thread.Sleep((int)wait);
                last = DateTime.UtcNow;
            }
            string body = "format=json&client=" + AppKey + "&meta=recordings+releasegroups+sources&duration=" + duration + "&fingerprint=" + Uri.EscapeDataString(fp);
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                wc.Headers[HttpRequestHeader.UserAgent] = MusicBrainz.UserAgent;
                try { return Json.Read(wc.UploadString(Api, body)); }
                catch (WebException e)
                {
                    if (e.Response == null) throw;
                    using (var r = new StreamReader(e.Response.GetResponseStream())) return Json.Read(r.ReadToEnd());
                }
            }
        }
    }
}
