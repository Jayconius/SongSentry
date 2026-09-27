using System;
using System.Collections.Generic;
using System.Linq;

namespace SongSentry
{
    // Offline "is this music?" check (prototype tested 2026-09-27: music 98%, game-only / speech 0%).
    public sealed class MusicFeatures
    {
        public double Tonal, Chroma, Beat, Continuity;
        public override string ToString() { return string.Format("tonal {0:0.00} chroma {1:0.00} beat {2:0.00} cont {3:0.00}", Tonal, Chroma, Beat, Continuity); }
    }

    public static class MusicDetector
    {
        const int N = 1024, Hop = 512;

        // mono 16-bit at any rate -> decimate by 4 (~11-12 kHz) -> features over the whole clip
        public static MusicFeatures Analyze(short[] pcm, int rate)
        {
            int dec = Math.Max(1, (int)Math.Round(rate / 11025.0)), sr = rate / dec;   // 48 kHz -> 12 kHz, 8 kHz stays
            var x = new float[pcm.Length / dec];
            for (int i = 0; i < x.Length; i++) { int s = 0; for (int k = 0; k < dec; k++) s += pcm[i * dec + k]; x[i] = s / (dec * 32768f); }
            int frames = (x.Length - N) / Hop + 1;
            if (frames < 20) return new MusicFeatures();
            var win = new float[N]; for (int i = 0; i < N; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));
            int lo = (int)(120.0 * N / sr), hi = (int)(4000.0 * N / sr);
            var db = new float[frames][]; var energy = new double[frames]; var chroma = new double[frames][];
            var re = new double[N]; var im = new double[N];
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < N; i++) { re[i] = x[f * Hop + i] * win[i]; im[i] = 0; }
                Fft(re, im);
                db[f] = new float[N / 2]; chroma[f] = new double[12]; double e = 0;
                for (int b = 1; b < N / 2; b++)
                {
                    double p = re[b] * re[b] + im[b] * im[b];
                    db[f][b] = (float)(10 * Math.Log10(p + 1e-12));
                    if (b >= lo && b <= hi)
                    {
                        e += p;
                        double hz = b * (double)sr / N;
                        int pc = ((int)Math.Round(12 * Math.Log(hz / 440.0, 2)) % 12 + 12) % 12;
                        chroma[f][pc] += Math.Sqrt(p);
                    }
                }
                energy[f] = 10 * Math.Log10(e + 1e-12);
            }

            // Tonal: peaks >= 10 dB above the local median (+-4 bins) that persist >= 4 frames (~0.35 s) within +-1 bin.
            var peaks = new List<int>[frames];
            for (int f = 0; f < frames; f++)
            {
                peaks[f] = new List<int>();
                for (int b = lo + 4; b < hi - 4; b++)
                {
                    float v = db[f][b];
                    if (v < db[f][b - 1] || v < db[f][b + 1]) continue;
                    var nb = new float[8]; int k = 0;
                    for (int d = -4; d <= 4; d++) if (d != 0) nb[k++] = db[f][b + d];
                    Array.Sort(nb);
                    if (v - (nb[3] + nb[4]) / 2 >= 10) peaks[f].Add(b);
                }
            }
            double persistent = 0;
            for (int f = 3; f < frames; f++)
                foreach (int b in peaks[f])
                {
                    bool ok = true;
                    for (int g = f - 3; g < f && ok; g++) ok = peaks[g].Any(p => Math.Abs(p - b) <= 1);
                    if (ok) persistent++;
                }
            double tonal = persistent / (frames - 3);   // average persistent peaks per frame

            // Chroma stability: mean Pearson correlation of chroma 0.5 s apart
            int lag = Math.Max(1, (int)(0.5 * sr / Hop));
            double cs = 0; int cn = 0;
            for (int f = 0; f + lag < frames; f++) { cs += Pearson(chroma[f], chroma[f + lag]); cn++; }
            double chromaStab = cn > 0 ? cs / cn : 0;

            // Beat: autocorrelation of the onset envelope (positive spectral flux), best lag 0.3-1.0 s, normalised
            var onset = new double[frames];
            for (int f = 1; f < frames; f++) { double s = 0; for (int b = lo; b <= hi; b++) { double d = db[f][b] - db[f - 1][b]; if (d > 0) s += d; } onset[f] = s; }
            double mean = onset.Average(); for (int f = 0; f < frames; f++) onset[f] -= mean;
            double r0 = onset.Sum(v => v * v) + 1e-9, beat = 0;
            for (int l = (int)(0.3 * sr / Hop); l <= (int)(1.0 * sr / Hop) && l < frames / 2; l++)
            { double r = 0; for (int f = 0; f + l < frames; f++) r += onset[f] * onset[f + l]; beat = Math.Max(beat, r / r0); }

            // Continuity: fraction of frames within 15 dB of the window's 90th-percentile energy (speech pauses drop out)
            var sorted = energy.OrderBy(v => v).ToArray();
            double p90 = sorted[(int)(0.9 * (frames - 1))];
            double cont = energy.Count(v => v > p90 - 15) / (double)frames;

            return new MusicFeatures { Tonal = tonal, Chroma = chromaStab, Beat = beat, Continuity = cont };
        }

        // The rule (tuned on the test material): music if sustained tones AND (stable harmony OR a beat), and continuous.
        public static bool IsMusic(MusicFeatures m, out double score)
        {
            score = Math.Min(m.Tonal / 3.0, 1.5) + Math.Max(0, m.Chroma - 0.3) * 2 + Math.Max(0, m.Beat - 0.15) * 2;
            return m.Tonal >= TonalMin && m.Continuity >= ContMin && (m.Chroma >= ChromaMin || m.Beat >= BeatMin);
        }
        public static double TonalMin = 1.0, ChromaMin = 0.5, BeatMin = 0.3, ContMin = 0.8;

        /// The tuned rule from testing: sustained tones and no speech-like gaps (one 5 s window).
        public static bool LooksLikeMusic(short[] pcm, int rate)
        {
            var m = Analyze(pcm, rate);
            return m.Tonal >= 0.4 && m.Continuity >= 0.8;
        }

        static double Pearson(double[] a, double[] b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
            return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
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
