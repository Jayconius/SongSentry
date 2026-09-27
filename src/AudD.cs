using System;
using System.IO;
using System.Net;
using System.Text;

namespace SongSentry
{
    public sealed class AudDResult
    {
        public bool Ok;              // the request worked (even if no song was found)
        public bool Found;
        public string Artist, Title, Album, Label, ReleaseDate, Timecode, Link, Error;
    }

    /// Optional, user-supplied AudD key (audd.io): recognises a short clip taken from anywhere in a song, also under
    /// game noise. Never ships with a key; the user adds their own in Settings.
    public static class AudD
    {
        const int SendRate = 16000;   // plenty for recognition, keeps uploads small (~32 KB/s)

        static AudD()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
        }

        /// Blocking; call from a worker. pcm = mono 16-bit at `rate`. Up to ~12 s is used.
        public static AudDResult Recognize(string apiToken, short[] pcm, int rate)
        {
            var res = new AudDResult();
            if (string.IsNullOrWhiteSpace(apiToken)) { res.Error = "No AudD key"; return res; }
            try
            {
                byte[] wav = Wav(Resample(pcm, rate, SendRate, 12), SendRate);
                string boundary = "----SongSentry" + DateTime.Now.Ticks.ToString("x");
                var body = new MemoryStream();
                Action<string> w = s => { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); };
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"api_token\"\r\n\r\n" + apiToken.Trim() + "\r\n");
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"return\"\r\n\r\nmusicbrainz\r\n");
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
                body.Write(wav, 0, wav.Length);
                w("\r\n--" + boundary + "--\r\n");

                var req = (HttpWebRequest)WebRequest.Create("https://api.audd.io/");
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                req.UserAgent = MusicBrainz.UserAgent;
                req.Timeout = 20000;
                byte[] all = body.ToArray();
                req.ContentLength = all.Length;
                using (var s = req.GetRequestStream()) s.Write(all, 0, all.Length);
                string json;
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = r.ReadToEnd();

                var d = Json.Read(json);
                if (Json.Str(d, "status") != "success")
                {
                    var err = Json.Obj(d.ContainsKey("error") ? d["error"] : null);
                    res.Error = err != null ? Json.Str(err, "error_message") : "AudD error";
                    return res;
                }
                res.Ok = true;
                var x = Json.Obj(d.ContainsKey("result") ? d["result"] : null);
                if (x == null) return res;
                res.Found = true;
                res.Artist = Json.Str(x, "artist"); res.Title = Json.Str(x, "title"); res.Album = Json.Str(x, "album");
                res.Label = Json.Str(x, "label"); res.ReleaseDate = Json.Str(x, "release_date");
                res.Timecode = Json.Str(x, "timecode"); res.Link = Json.Str(x, "song_link");
            }
            catch (Exception e) { res.Error = e.Message; }
            return res;
        }

        static short[] Resample(short[] x, int from, int to, int maxSeconds)
        {
            int n = (int)Math.Min((long)x.Length * to / from, (long)to * maxSeconds);
            var o = new short[n];
            double step = (double)from / to;
            int span = Math.Max(1, (int)Math.Round(step));
            for (int i = 0; i < n; i++)
            {
                int s0 = (int)(i * step);
                int sum = 0, cnt = 0;
                for (int k = 0; k < span && s0 + k < x.Length; k++) { sum += x[s0 + k]; cnt++; }   // box filter against aliasing
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
