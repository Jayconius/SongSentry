// README / GitHub graphics drawn from SongSentry's own theme: banner, "how it works" diagram, social preview (1280x640).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace SongSentry
{
    static class Graphics2
    {
        public static void RenderAll(string dir)
        {
            Directory.CreateDirectory(dir);
            Save(Path.Combine(dir, "banner.png"), 1280, 360, (g, w, h) => Banner(g, w, h, false));
            Save(Path.Combine(dir, "social-preview.png"), 1280, 640, (g, w, h) => Banner(g, w, h, true));
            Save(Path.Combine(dir, "how-it-works.png"), 1280, 560, HowItWorks);
        }

        static void Save(string path, int w, int h, Action<Graphics, int, int> draw)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    draw(g, w, h);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static void Glow(Graphics g, float cx, float cy, float rx, float ry, Color c, int alpha)
        {
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
                using (var pb = new PathGradientBrush(gp) { CenterColor = Theme.A(c, alpha), SurroundColors = new[] { Theme.A(c, 0) } })
                    g.FillPath(pb, gp);
            }
        }

        static void Banner(Graphics g, int w, int h, bool social)
        {
            g.Clear(Theme.Bg);
            Glow(g, w * 0.22f, h * 0.5f, w * 0.45f, h * 0.9f, Theme.Accent, 60);
            Glow(g, w * 0.85f, h * 0.2f, w * 0.35f, h * 0.7f, Theme.Blue, 30);
            // faint waveform across the background
            using (var pen = new Pen(Theme.A(Theme.Accent, 26), 2f))
            {
                var rnd = new Random(3);
                float mid = h * 0.78f, prev = mid;
                for (float x = 0; x < w; x += 6)
                {
                    float amp = (float)(Math.Sin(x / 37.0) * Math.Sin(x / 211.0)) * h * 0.09f * (0.6f + (float)rnd.NextDouble() * 0.4f);
                    g.DrawLine(pen, x, mid - amp, x, mid + amp);
                }
            }
            float s = social ? 230 : 170;
            float lx = social ? 110 : 90, ly = (h - s) / 2 - (social ? 20 : 0);
            Theme.DrawLogo(g, new RectangleF(lx, ly, s, s * 1.02f));
            float tx = lx + s + (social ? 60 : 44);
            using (var fName = new Font("Segoe UI Semibold", social ? 96 : 72, GraphicsUnit.Pixel))
            using (var fTag = new Font("Segoe UI", social ? 34 : 26, GraphicsUnit.Pixel))
            using (var fBeta = new Font("Segoe UI Semibold", social ? 22 : 17, GraphicsUnit.Pixel))
            using (var fSmall = new Font("Segoe UI", social ? 24 : 18, GraphicsUnit.Pixel))
            {
                float ny = ly + (social ? 6 : 0);
                using (var b = new SolidBrush(Theme.Text)) g.DrawString("SongSentry", fName, b, tx - 8, ny);
                SizeF nsz = g.MeasureString("SongSentry", fName);
                var beta = new RectangleF(tx + nsz.Width - 14, ny + nsz.Height * 0.28f, social ? 84 : 66, social ? 34 : 27);
                Theme.Stroke(g, beta, beta.Height / 2, Theme.Accent, 2);
                Theme.Text2(g, "BETA", fBeta, Theme.Accent, beta, StringAlignment.Center, StringAlignment.Center);
                using (var b = new SolidBrush(Theme.Sub))
                    g.DrawString("Keeps licensed music off your stream.", fTag, b, tx, ny + nsz.Height - (social ? 4 : 2));
                using (var b = new SolidBrush(Theme.Dim))
                    g.DrawString(social ? "Mutes licensed songs in your OBS sources  ·  free  ·  Windows" : "Recognises songs in your OBS sources and mutes them on stream  ·  free  ·  Windows",
                                 fSmall, b, tx + 2, ny + nsz.Height + (social ? 50 : 38));
                if (social)
                {
                    float px = tx;
                    foreach (string chip in new[] { "Spotify & browsers", "Game audio", "OBS", "Offline song memory" })
                    {
                        SizeF cs = g.MeasureString(chip, fSmall);
                        var r = new RectangleF(px, ny + nsz.Height + 110, cs.Width + 34, 46);
                        Theme.Fill(g, r, 23, Theme.A(Theme.Accent, 36));
                        Theme.Text2(g, chip, fSmall, Theme.AccentHi, r, StringAlignment.Center, StringAlignment.Center);
                        px = r.Right + 14;
                    }
                }
            }
        }

        static void HowItWorks(Graphics g, int w, int h)
        {
            g.Clear(Theme.Bg);
            Glow(g, w * 0.5f, h * 0.5f, w * 0.55f, h * 0.8f, Theme.Accent, 22);
            using (var fT = new Font("Segoe UI Semibold", 30, GraphicsUnit.Pixel))
            using (var fH = new Font("Segoe UI Semibold", 20, GraphicsUnit.Pixel))
            using (var fB = new Font("Segoe UI", 15.5f, GraphicsUnit.Pixel))
            using (var fI = new Font(Theme.IconFont, 26, GraphicsUnit.Pixel))
            {
                Theme.Text2(g, "How SongSentry works", fT, Theme.Text, new RectangleF(0, 26, w, 40), StringAlignment.Center, StringAlignment.Center);

                // column 1: what it listens to
                Card(g, new RectangleF(40, 100, 300, 190), Theme.GMusic, "Now Playing", "Spotify, YouTube Music and browsers tell Windows what's playing. " +
                     "Instant, even mid-song.", fH, fB, fI, Theme.Blue);
                Card(g, new RectangleF(40, 316, 300, 190), Theme.GVolume, "The source's audio", "It listens to each protected OBS source: per app or " +
                     "its sound device, including game audio.", fH, fB, fI, Theme.Blue);
                // column 2: recognition
                Card(g, new RectangleF(490, 100, 300, 190), Theme.GPulse, "Song memory", "Offline landmark fingerprints. Recognises a known song in ~5 s, " +
                     "mid-song, even under loud game sound.", fH, fB, fI, Theme.Accent);
                Card(g, new RectangleF(490, 316, 300, 190), Theme.GGlobe, "Online (optional)", "Unknown music? Your own free AudioTag or AudD key names it " +
                     "once; then it's learned.", fH, fB, fI, Theme.Accent);
                // column 3: action
                Card(g, new RectangleF(940, 100, 300, 406), Theme.GMute, "Acts in OBS", "", fH, fB, fI, Theme.Coral);
                string[] lines = { "Mute on stream only", "Mute everywhere", "Turn down", "Warn only", "",
                                   "Your recording keeps the music; your allow list (artists, songs, labels) is never touched.", "", "Restores the source when the song is over." };
                float ly = 176;
                foreach (string l in lines)
                {
                    if (l.Length > 0)
                    {
                        bool bullet = ly < 300;
                        if (bullet) using (var b = new SolidBrush(Theme.Coral)) g.FillEllipse(b, 966, ly + 8, 7, 7);
                        Theme.Wrap(g, l, fB, bullet ? Theme.Text : Theme.Sub, new RectangleF(bullet ? 984 : 966, ly, bullet ? 240 : 256, 72));
                    }
                    ly += ly >= 300 ? (l.Length > 60 ? 68 : l.Length > 36 ? 44 : 28) : 28;
                }
                // arrows
                Arrow(g, 346, 195, 484, 195); Arrow(g, 346, 411, 484, 411);
                Arrow(g, 346, 205, 484, 400, true);
                Arrow(g, 796, 195, 934, 280); Arrow(g, 796, 411, 934, 330);
                // online answers are learned into the memory
                using (var pen = new Pen(Theme.A(Theme.Accent, 170), 2.5f) { DashStyle = DashStyle.Dash })
                {
                    pen.CustomEndCap = new AdjustableArrowCap(5, 6);
                    g.DrawLine(pen, 640, 314, 640, 294);
                }
                Theme.Text2(g, "↑  every song it identifies is learned, then recognised offline", fB, Theme.Accent, new RectangleF(390, 518, 500, 24), StringAlignment.Center, StringAlignment.Center);
            }
        }

        static void Card(Graphics g, RectangleF r, string glyph, string title, string body, Font fH, Font fB, Font fI, Color tone)
        {
            Theme.Fill(g, r, 18, Theme.Panel);
            Theme.Stroke(g, r, 18, Theme.A(tone, 90), 1.5f);
            var ic = new RectangleF(r.X + 22, r.Y + 22, 46, 46);
            Theme.Fill(g, ic, 23, Theme.A(tone, 40));
            Theme.Text2(g, glyph, fI, tone, ic, StringAlignment.Center, StringAlignment.Center);
            Theme.Text2(g, title, fH, Theme.Text, new RectangleF(r.X + 82, r.Y + 22, r.Width - 96, 46), StringAlignment.Near, StringAlignment.Center);
            Theme.Wrap(g, body, fB, Theme.Sub, new RectangleF(r.X + 22, r.Y + 84, r.Width - 44, r.Height - 96));
        }

        static void Arrow(Graphics g, float x1, float y1, float x2, float y2, bool faint = false)
        {
            using (var pen = new Pen(Theme.A(Theme.Sub, faint ? 60 : 140), 2.5f))
            {
                pen.CustomEndCap = new AdjustableArrowCap(5, 6);
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }
    }
}
