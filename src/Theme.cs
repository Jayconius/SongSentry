using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SongSentry
{
    /// SongSentry's own look: deep slate, a signal teal-green "sentry" accent, amber for warnings, coral for "acting now".
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(11, 14, 20);
        public static readonly Color Side = Color.FromArgb(15, 19, 27);
        public static readonly Color Panel = Color.FromArgb(21, 26, 35);
        public static readonly Color Raised = Color.FromArgb(29, 35, 47);
        public static readonly Color Hover = Color.FromArgb(37, 44, 59);
        public static readonly Color Line = Color.FromArgb(38, 46, 62);
        public static readonly Color Text = Color.FromArgb(230, 234, 242);
        public static readonly Color Sub = Color.FromArgb(147, 160, 180);
        public static readonly Color Dim = Color.FromArgb(94, 106, 126);
        public static readonly Color Accent = Color.FromArgb(45, 212, 167);
        public static readonly Color AccentHi = Color.FromArgb(94, 234, 196);
        public static readonly Color AccentInk = Color.FromArgb(6, 40, 32);
        public static readonly Color Amber = Color.FromArgb(245, 181, 68);
        public static readonly Color Coral = Color.FromArgb(255, 92, 122);
        public static readonly Color Blue = Color.FromArgb(90, 169, 255);
        public static readonly Color SwitchOff = Color.FromArgb(52, 61, 80);

        public const string IconFont = "Segoe MDL2 Assets";
        public const string GPlay = "", GPause = "", GSettings = "", GRefresh = "", GCheck = "",
            GClose = "", GMin = "", GChevron = "", GWarn = "", GMusic = "", GVolume = "",
            GMute = "", GAdd = "", GDelete = "", GCancel = "", GInfo = "", GLink = "",
            GPulse = "", GList = "", GGame = "", GGlobe = "", GMic = "", GFilm = "";

        public const string GView = "", GHide = "";

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color A(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c); }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w = 1f)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        public static void Text2(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h, StringAlignment v)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = h; sf.LineAlignment = v; sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(s ?? "", f, b, r, sf);
            }
        }

        public static void Wrap(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat()) { sf.Trimming = StringTrimming.EllipsisWord; g.DrawString(s ?? "", f, b, r, sf); }
        }

        /// The shield outline used by the logo and the big status badge.
        public static GraphicsPath ShieldPath(RectangleF r)
        {
            Func<float, float, PointF> P = (x, y) => new PointF(r.X + x * r.Width, r.Y + y * r.Height);
            var p = new GraphicsPath();
            p.AddBezier(P(0.50f, 0.03f), P(0.62f, 0.11f), P(0.78f, 0.15f), P(0.93f, 0.16f));
            p.AddBezier(P(0.93f, 0.16f), P(0.95f, 0.55f), P(0.84f, 0.80f), P(0.50f, 0.97f));
            p.AddBezier(P(0.50f, 0.97f), P(0.16f, 0.80f), P(0.05f, 0.55f), P(0.07f, 0.16f));
            p.AddBezier(P(0.07f, 0.16f), P(0.22f, 0.15f), P(0.38f, 0.11f), P(0.50f, 0.03f));
            p.CloseFigure();
            return p;
        }

        /// A beamed pair of eighth notes.
        public static void DrawNote(Graphics g, RectangleF r, Color c)
        {
            Func<float, float, PointF> P = (x, y) => new PointF(r.X + x * r.Width, r.Y + y * r.Height);
            float stem = Math.Max(1f, r.Width * 0.085f);
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, stem))
            {
                pen.StartCap = pen.EndCap = LineCap.Flat;
                g.FillPolygon(b, new[] { P(0.30f, 0.10f), P(0.92f, 0.00f), P(0.92f, 0.20f), P(0.30f, 0.30f) });   // beam
                g.DrawLine(pen, P(0.34f, 0.18f), P(0.34f, 0.80f));
                g.DrawLine(pen, P(0.88f, 0.08f), P(0.88f, 0.70f));
                FillHead(g, b, P(0.20f, 0.80f), r.Width * 0.30f, r.Height * 0.22f);
                FillHead(g, b, P(0.74f, 0.70f), r.Width * 0.30f, r.Height * 0.22f);
            }
        }

        static void FillHead(Graphics g, Brush b, PointF center, float w, float h)
        {
            var st = g.Save();
            g.TranslateTransform(center.X, center.Y);
            g.RotateTransform(-22);
            g.FillEllipse(b, -w / 2, -h / 2, w, h);
            g.Restore(st);
        }

        /// App logo: teal shield with a white double note. Works from 16 px (tray) to 256 px.
        public static void DrawLogo(Graphics g, RectangleF r)
        {
            using (var shield = ShieldPath(r))
            using (var grad = new LinearGradientBrush(r, Color.FromArgb(94, 234, 196), Color.FromArgb(14, 150, 136), 60f))
            {
                g.FillPath(grad, shield);
                if (r.Width >= 40)
                {
                    var top = new RectangleF(r.X, r.Y - 1, r.Width, r.Height * 0.5f + 2);
                    using (var hl = new LinearGradientBrush(top, Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    {
                        var st = g.Save();
                        g.SetClip(shield);
                        g.FillRectangle(hl, top);
                        g.Restore(st);
                    }
                }
            }
            float s = r.Width <= 20 ? 0.56f : 0.46f;
            var nr = new RectangleF(r.X + r.Width * (0.5f - s / 2) + r.Width * 0.01f, r.Y + r.Height * (r.Width <= 20 ? 0.22f : 0.26f), r.Width * s, r.Height * s);
            DrawNote(g, nr, r.Width <= 20 ? Color.White : Color.FromArgb(250, 255, 255, 255));
        }

        public static Icon MakeIcon(int size)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    DrawLogo(g, new RectangleF(0.5f, 0.5f, size - 1, size - 1));
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    /// Dark dropdown menus for ContextMenuStrip.
    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Color.White : Theme.Text) : Theme.Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Sub;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using (var f = new Font(Theme.IconFont, 10f, GraphicsUnit.Pixel))
                Theme.Text2(e.Graphics, Theme.GCheck, f, Theme.Accent, r, StringAlignment.Center, StringAlignment.Center);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Raised; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Raised; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Raised; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Raised; } }
            public override Color MenuBorder { get { return Theme.Line; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.Hover; } }
            public override Color SeparatorDark { get { return Theme.Line; } }
            public override Color SeparatorLight { get { return Theme.Raised; } }
            public override Color CheckBackground { get { return Theme.Raised; } }
            public override Color CheckSelectedBackground { get { return Theme.Hover; } }
            public override Color CheckPressedBackground { get { return Theme.Hover; } }
        }
    }
}
