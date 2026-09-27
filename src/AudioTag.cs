using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SongSentry
{
    /// Optional, user-supplied AudioTag.info key: free for ~1.5 h of audio per month per account.
    /// Asynchronous API: "identify" uploads a clip and returns a token, "get_result" is polled until found / not found.
    /// Never ships with a key; the user adds their own in Settings. Results are returned as an AudDResult-shaped object.
    public static class AudioTag
    {
        const string Api = "https://audiotag.info/api";
        const int SendRate = 8000;   // AudioTag's native format: 16-bit PCM WAV, 8 kHz, mono (fastest, smallest)
        public const int MinSeconds = 12;   // documented minimum is 5 s, but in testing (2026-09-27) 10 s was rejected as "too short"; 12 s works

        static AudioTag()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
        }

        /// Blocking; call from a worker. pcm = mono 16-bit at `rate`; 12-15 s is used. Costs 1 free second per second sent.
        public static AudDResult Recognize(string apiKey, short[] pcm, int rate, int timeoutMs = 20000)
        {
            var res = new AudDResult();
            if (string.IsNullOrWhiteSpace(apiKey)) { res.Error = "No AudioTag key"; return res; }
            try
            {
                if (pcm.Length < rate * MinSeconds) { res.Error = "AudioTag needs at least " + MinSeconds + " s of audio"; return res; }
                byte[] wav = Wav(Resample(pcm, rate, SendRate, 15), SendRate);
                var up = Json.Read(PostMultipart(Json.Make("apikey", apiKey.Trim(), "action", "identify"), wav));
                if (!Json.Bool(up, "success", false)) { res.Error = Json.Str(up, "error") ?? "AudioTag error"; return res; }
                string token = Json.Str(up, "token");
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(700);
                    var r = Json.Read(PostForm(Json.Make("apikey", apiKey.Trim(), "action", "get_result", "token", token)));
                    string state = Json.Str(r, "result");
                    if (state == "wait") continue;
                    if (!Json.Bool(r, "success", false) && state == null) { res.Error = Json.Str(r, "error"); return res; }
                    res.Ok = true;
                    if (state != "found") return res;   // "not found"
                    // data: [{ confidence, time, tracks: [[title, artist, album, year], ...] }, ...] - take the best one
                    Dictionary best = null;
                    foreach (var cand in Json.Objs(r.ContainsKey("data") ? r["data"] : null))
                        if (best == null || Json.Num(cand, "confidence", 0) > Json.Num(best.D, "confidence", 0)) best = new Dictionary(cand);
                    if (best == null) return res;
                    var tracks = best.D.ContainsKey("tracks") ? best.D["tracks"] as ArrayList : null;
                    var first = tracks != null && tracks.Count > 0 ? tracks[0] as ArrayList : null;
                    if (first == null || first.Count < 2) return res;
                    res.Found = true;
                    res.Title = Convert.ToString(first[0]); res.Artist = Convert.ToString(first[1]);
                    res.Album = first.Count > 2 ? Convert.ToString(first[2]) : null;
                    res.ReleaseDate = first.Count > 3 ? Convert.ToString(first[3]) : null;
                    res.Timecode = Json.Str(best.D, "time") + " (confidence " + Json.Str(best.D, "confidence") + ")";
                    return res;
                }
                res.Error = "AudioTag took too long";
            }
            catch (Exception e) { res.Error = e.Message; }
            return res;
        }

        sealed class Dictionary
        {
            public readonly System.Collections.Generic.Dictionary<string, object> D;
            public Dictionary(System.Collections.Generic.Dictionary<string, object> d) { D = d; }
        }

        static string PostForm(System.Collections.Generic.Dictionary<string, object> fields)
        {
            var sb = new StringBuilder();
            foreach (var kv in fields) sb.Append(sb.Length > 0 ? "&" : "").Append(kv.Key).Append('=').Append(Uri.EscapeDataString(Convert.ToString(kv.Value)));
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                wc.Headers[HttpRequestHeader.UserAgent] = MusicBrainz.UserAgent;
                wc.Encoding = Encoding.UTF8;
                return wc.UploadString(Api, sb.ToString());
            }
        }

        static string PostMultipart(System.Collections.Generic.Dictionary<string, object> fields, byte[] wav)
        {
            string boundary = "----SongSentry" + DateTime.Now.Ticks.ToString("x");
            var body = new MemoryStream();
            Action<string> w = s => { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); };
            foreach (var kv in fields)
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + kv.Key + "\"\r\n\r\n" + kv.Value + "\r\n");
            w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            body.Write(wav, 0, wav.Length);
            w("\r\n--" + boundary + "--\r\n");
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.UserAgent = MusicBrainz.UserAgent;
            req.Timeout = 20000;
            byte[] all = body.ToArray();
            req.ContentLength = all.Length;
            using (var s = req.GetRequestStream()) s.Write(all, 0, all.Length);
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        static short[] Resample(short[] x, int from, int to, int maxSeconds)
        {
            int n = (int)Math.Min((long)x.Length * to / from, (long)to * maxSeconds);
            var o = new short[n];
            double step = (double)from / to;
            int span = Math.Max(1, (int)Math.Round(step));
            for (int i = 0; i < n; i++)
            {
                int s0 = (int)(i * step), sum = 0, cnt = 0;
                for (int k = 0; k < span && s0 + k < x.Length; k++) { sum += x[s0 + k]; cnt++; }
                o[i] = (short)(cnt > 0 ? sum / cnt : 0);
            }
            return o;
        }

        static byte[] Wav(short[] pcm, int rate)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + pcm.Length * 2); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(pcm.Length * 2);
            foreach (short s in pcm) bw.Write(s);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
