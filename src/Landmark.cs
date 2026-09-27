using System;
using System.Collections.Generic;
using System.Linq;

namespace SongSentry
{
    /// Landmark ("constellation") audio fingerprinting, after Wang (2003), the published technique behind Shazam,
    /// Panako and Olaf. Written from the paper for SongSentry (no third-party code). Spectrogram peaks are paired into
    /// hashes (f1, f2, dt); a clip matches a stored track when many hashes agree on the same time offset. Peaks survive
    /// loud broadband noise (gunfire, explosions) better than whole-spectrum fingerprints.
    public sealed class LandmarkIndex
    {
        public const int Rate = 8000, N = 1024, Hop = 256;          // 128 ms windows, 32 ms hop
        public const double FrameSec = (double)Hop / Rate;
        const int FMin = 12, FMax = 450;                             // ~94 Hz .. ~3.5 kHz
        const int NbF = 10, NbT = 5;                                 // peak neighbourhood: +-10 bins, +-5 frames
        const int MaxDt = 40, MaxDf = 110;
        public static int PeaksPerSecond = 14, FanOut = 5;           // density: ~50 hashes per second of audio
        public const int MaxTracks = 65535, MaxFrame = 65535;        // 16-bit fields: up to ~35 min per track

        public sealed class Track { public int Id; public string Name; public int Frames; public object Tag; }
        public sealed class Match
        {
            public Track Track; public int Votes, Hashes, Second;
            public double OffsetSec;                                 // where in the track the clip starts
        }

        // Compact index: one sorted ulong per hash occurrence = hash(22 bits) << 32 | track(16) << 16 | frame(16).
        // 8 bytes per entry, binary-searched; new entries collect in `pending` and are merged in before the next query.
        ulong[] sorted = new ulong[0];
        readonly List<ulong> pending = new List<ulong>();
        public readonly List<Track> Tracks = new List<Track>();
        public int HashCount { get { return sorted.Length + pending.Count; } }

        public Track AddTrack(string name, object tag = null)
        {
            if (Tracks.Count >= MaxTracks) throw new InvalidOperationException("Song memory is full");
            var t = new Track { Id = Tracks.Count, Name = name, Tag = tag };
            Tracks.Add(t);
            return t;
        }

        /// Adds a whole track (mono 16-bit at any rate).
        public Track Add(string name, short[] pcm, int rate, object tag = null)
        {
            var t = AddTrack(name, tag);
            Learn(t, pcm, rate, 0);
            return t;
        }

        /// Learns a piece of a track that starts `startFrame` frames into the song. Returns the number of hashes added.
        public int Learn(Track t, short[] pcm, int rate, int startFrame) { return Learn(t, pcm, rate, startFrame, int.MinValue, int.MaxValue); }

        /// As above, but skips hashes anchored before `minFrame` (song position), so overlapping chunks aren't learned twice.
        /// Anchor frames of a chunk that have their full pairing window inside it (edges lack neighbours / targets).
        public static int EdgeStartFrames { get { return NbT + 2; } }
        public static int EdgeEndFrames { get { return MaxDt + NbT + 2; } }

        public int Learn(Track t, short[] pcm, int rate, int startFrame, int minFrame, int maxFrame)
        {
            int frames;
            var hashes = Hashes(pcm, rate, out frames);
            int added = 0;
            foreach (var h in hashes)
            {
                int f = h.Value + startFrame;
                if (f < 0 || f > MaxFrame || f < minFrame || f > maxFrame) continue;
                pending.Add(((ulong)(uint)h.Key << 32) | ((ulong)(uint)t.Id << 16) | (uint)f);
                added++;
            }
            t.Frames = Math.Max(t.Frames, Math.Min(MaxFrame, startFrame + frames));
            return added;
        }

        void Merge()
        {
            if (pending.Count == 0) return;
            pending.Sort();
            var m = new ulong[sorted.Length + pending.Count];
            int i = 0, j = 0, k = 0;
            while (i < sorted.Length && j < pending.Count) m[k++] = sorted[i] <= pending[j] ? sorted[i++] : pending[j++];
            while (i < sorted.Length) m[k++] = sorted[i++];
            while (j < pending.Count) m[k++] = pending[j++];
            sorted = m;
            pending.Clear();
        }

        /// All entries (for saving). Merges pending ones first.
        public ulong[] Entries() { Merge(); return sorted; }

        public void Load(IEnumerable<Track> tracks, ulong[] entries)
        {
            Tracks.Clear(); Tracks.AddRange(tracks);
            pending.Clear();
            sorted = entries ?? new ulong[0];
            Array.Sort(sorted);
        }

        public void Clear() { Tracks.Clear(); pending.Clear(); sorted = new ulong[0]; }

        /// Removes every entry of one track (e.g. user deleted a song from the memory). The track slot stays (renamed).
        public void Forget(Track t)
        {
            Merge();
            sorted = sorted.Where(e => (int)((e >> 16) & 0xffff) != t.Id).ToArray();
            t.Frames = 0;
        }

        /// Best match for a clip. Votes = hashes agreeing on one (track, offset); Second = best other candidate.
        public Match Query(short[] pcm, int rate)
        {
            Merge();
            int frames;
            var hashes = Hashes(pcm, rate, out frames);
            var votes = new Dictionary<long, int>();
            foreach (var h in hashes)
            {
                ulong lo = (ulong)(uint)h.Key << 32;
                int a = LowerBound(lo);
                for (int i = a; i < sorted.Length && (sorted[i] >> 32) == (uint)h.Key; i++)
                {
                    int track = (int)((sorted[i] >> 16) & 0xffff), frame = (int)(sorted[i] & 0xffff);
                    // bucket offsets in 2-frame steps so tiny timing jitter still votes together
                    long key = ((long)track << 32) | (uint)((frame - h.Value + 1000000) / 2);
                    int v; votes.TryGetValue(key, out v); votes[key] = v + 1;
                }
            }
            if (votes.Count == 0) return new Match { Hashes = hashes.Count };
            long bestKey = 0; int best = 0;
            foreach (var kv in votes)
            {
                int v = kv.Value, n;
                if (votes.TryGetValue(kv.Key + 1, out n)) v += n;   // neighbouring bucket (jitter across the edge)
                if (v > best) { best = v; bestKey = kv.Key; }
            }
            int bestTrack = (int)(bestKey >> 32);
            int second = 0;
            foreach (var kv in votes)
            {
                bool near = (int)(kv.Key >> 32) == bestTrack && Math.Abs((kv.Key & 0xffffffff) - (bestKey & 0xffffffff)) <= 2;
                if (!near && kv.Value > second) second = kv.Value;
            }
            int offFrames = (int)((bestKey & 0xffffffff) * 2 - 1000000);
            return new Match { Track = Tracks[bestTrack], Votes = best, Second = second, Hashes = hashes.Count, OffsetSec = offFrames * FrameSec };
        }

        int LowerBound(ulong v)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (sorted[mid] < v) lo = mid + 1; else hi = mid; }
            return lo;
        }
        // ------------------------------------------------------------------ fingerprint extraction

        /// (hash, anchor frame) pairs.
        public static List<KeyValuePair<int, int>> Hashes(short[] pcm, int rate, out int frames)
        {
            float[] x = Resample(pcm, rate);
            frames = x.Length >= N ? (x.Length - N) / Hop + 1 : 0;
            var peaks = Peaks(x, frames);
            var list = new List<KeyValuePair<int, int>>(peaks.Count * FanOut);
            for (int i = 0; i < peaks.Count; i++)
            {
                int t1 = peaks[i].T, f1 = peaks[i].F, made = 0;
                for (int j = i + 1; j < peaks.Count && made < FanOut; j++)
                {
                    int dt = peaks[j].T - t1;
                    if (dt < 1) continue;
                    if (dt > MaxDt) break;
                    int f2 = peaks[j].F;
                    if (Math.Abs(f2 - f1) > MaxDf) continue;
                    int h = ((f1 >> 1) << 14) | ((f2 >> 1) << 6) | dt;   // 8 + 8 + 6 bits
                    list.Add(new KeyValuePair<int, int>(h, t1));
                    made++;
                }
            }
            return list;
        }

        struct Peak { public int T, F; public float V; }

        static List<Peak> Peaks(float[] x, int frames)
        {
            int bins = N / 2;
            var spec = new float[frames][];
            var win = new double[N];
            for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            var re = new double[N]; var im = new double[N];
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < N; i++) { re[i] = x[f * Hop + i] * win[i]; im[i] = 0; }
                Fft(re, im);
                var s = new float[bins];
                for (int b = 0; b < bins; b++) s[b] = (float)(10 * Math.Log10(re[b] * re[b] + im[b] * im[b] + 1e-10));
                spec[f] = s;
            }
            // 2-D local maxima above the frame's mean level
            var cands = new List<Peak>();
            var fmax = new float[frames][];
            for (int f = 0; f < frames; f++)
            {
                var s = spec[f]; var m = new float[bins];
                for (int b = FMin; b < FMax; b++)
                {
                    float v = float.MinValue;
                    for (int k = Math.Max(FMin, b - NbF); k <= Math.Min(FMax - 1, b + NbF); k++) if (s[k] > v) v = s[k];
                    m[b] = v;
                }
                fmax[f] = m;
            }
            for (int f = 0; f < frames; f++)
            {
                double mean = 0; for (int b = FMin; b < FMax; b++) mean += spec[f][b]; mean /= (FMax - FMin);
                for (int b = FMin; b < FMax; b++)
                {
                    float v = spec[f][b];
                    if (v != fmax[f][b] || v < mean + 6) continue;
                    bool isMax = true;
                    for (int g = Math.Max(0, f - NbT); g <= Math.Min(frames - 1, f + NbT) && isMax; g++)
                        if (g != f && fmax[g][b] > v) isMax = false;
                    if (isMax) cands.Add(new Peak { T = f, F = b, V = (float)(v - mean) });
                }
            }
            // density cap: strongest PeaksPerSecond in each 1-second block
            int block = (int)Math.Round(1 / FrameSec);
            var kept = new List<Peak>();
            foreach (var g in cands.GroupBy(p => p.T / block))
                kept.AddRange(g.OrderByDescending(p => p.V).Take(PeaksPerSecond));
            kept.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.F.CompareTo(b.F));
            return kept;
        }

        /// Any rate -> 8 kHz mono float, with a crude box low-pass against aliasing.
        static float[] Resample(short[] pcm, int rate)
        {
            if (rate == Rate) return pcm.Select(s => s / 32768f).ToArray();
            double step = (double)rate / Rate;
            int n = (int)(pcm.Length / step), span = Math.Max(1, (int)Math.Round(step));
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                int s0 = (int)(i * step); float sum = 0; int c = 0;
                for (int k = 0; k < span && s0 + k < pcm.Length; k++) { sum += pcm[s0 + k]; c++; }
                o[i] = c > 0 ? sum / c / 32768f : 0;
            }
            return o;
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
        }
    }
}
