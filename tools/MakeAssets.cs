// Renders SongSentry's icon (.ico with 16-256 px PNG frames) from the app's own drawing code.
// Usage: MakeAssets <repo root>
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SongSentry
{
    static class MakeAssets
    {
        static int Main(string[] a)
        {
            string assets = Path.Combine(a[0], "assets");
            Directory.CreateDirectory(assets);
            var frames = new List<byte[]>();
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            foreach (int s in sizes)
            {
                using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float pad = s >= 48 ? s * 0.04f : 0.5f;
                        Theme.DrawLogo(g, new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad));
                    }
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); frames.Add(ms.ToArray()); }
                    if (s == 256) bmp.Save(Path.Combine(assets, "logo-256.png"), ImageFormat.Png);
                }
            }
            using (var fs = File.Create(Path.Combine(assets, "icon.ico")))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
                int offset = 6 + 16 * frames.Count;
                for (int i = 0; i < frames.Count; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(frames[i].Length); w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
            }
            return 0;
        }
    }
}
