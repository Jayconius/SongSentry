// Renders the logo at several sizes plus the MDL2 icon glyphs used by the UI, for a visual check.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;

namespace SongSentry
{
    static class GlyphPreview
    {
        static void Main(string[] a)
        {
            using (var bmp = new Bitmap(900, 480))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Theme.Bg);
                float x = 20;
                foreach (int s in new[] { 16, 20, 32, 48, 64, 128, 180 }) { Theme.DrawLogo(g, new RectangleF(x, 20, s, s)); x += s + 20; }
                using (var f = new Font(Theme.IconFont, 22f, GraphicsUnit.Pixel))
                using (var lf = new Font("Segoe UI", 11f, GraphicsUnit.Pixel))
                {
                    int i = 0;
                    foreach (FieldInfo fi in typeof(Theme).GetFields())
                    {
                        if (!fi.Name.StartsWith("G") || fi.FieldType != typeof(string)) continue;
                        float gx = 20 + (i % 8) * 108, gy = 230 + (i / 8) * 60;
                        Theme.Text2(g, (string)fi.GetValue(null), f, Theme.Text, new RectangleF(gx, gy, 30, 30), StringAlignment.Center, StringAlignment.Center);
                        Theme.Text2(g, fi.Name.Substring(1), lf, Theme.Sub, new RectangleF(gx + 32, gy + 6, 76, 20), StringAlignment.Near, StringAlignment.Center);
                        i++;
                    }
                }
                bmp.Save(a[0]);
            }
        }
    }
}
