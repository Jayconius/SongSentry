using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SongSentry
{
    public enum Page { Live, Channels, Recognition, SafeMusic, Playlists, Settings }

    public sealed class MainForm : Form
    {
        public const float W = 980f, H = 640f, SideW = 224f;
        const float X0 = SideW + 28f, CW = W - SideW - 56f, HeadH = 92f;

        readonly Settings settings;
        readonly Engine engine;
        readonly ObsConnection obs;
        readonly NowPlayingWatcher nowPlaying;
        readonly Recognizer recognizer;
        readonly PlaylistScanner scanner;
        string scanApp, plStatus, scanTarget;
        bool plBusy;
        readonly List<KeyValuePair<string, RectangleF>> hits = new List<KeyValuePair<string, RectangleF>>();
        readonly Dictionary<string, float> knobs = new Dictionary<string, float>();
        readonly Font fBrand, fH1, fH2, fBody, fBodyB, fSmall, fTiny, fCaps, fIcon, fIconS, fIconL;
        readonly Timer anim, secondTick;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip trayMenu;
        readonly TextBox tbHost, tbPort, tbPassword, tbAllow, tbSearch, tbDelay, tbAudioTag, tbAudD;
        readonly ToolTip tip = new ToolTip();
        float scale = 1f, scroll, scrollMax, pulse;
        string hover, toast;
        DateTime toastUntil;
        bool repaintQueued, quitting, trayHintShown;
        Page page = Page.Live;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendMessageStr(IntPtr h, int msg, IntPtr w, string l);

        public MainForm(Settings settings, Engine engine, ObsConnection obs, NowPlayingWatcher nowPlaying, Recognizer recognizer)
        {
            this.settings = settings; this.engine = engine; this.obs = obs; this.nowPlaying = nowPlaying; this.recognizer = recognizer;
            scanner = new PlaylistScanner(nowPlaying.Snapshot, nowPlaying.SkipNext, ScanFound);
            scanner.Changed += () => { if (!scanner.Running) ScanFinished(); QueueRepaint(); };
            Text = "SongSentry";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size((int)Math.Ceiling(W * scale), (int)Math.Ceiling(H * scale));

            fBrand = new Font("Segoe UI Semibold", 17f, GraphicsUnit.Pixel);
            fH1 = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Pixel);
            fH2 = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
            fBody = new Font("Segoe UI", 13.5f, GraphicsUnit.Pixel);
            fBodyB = new Font("Segoe UI Semibold", 13.5f, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
            fTiny = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);
            fCaps = new Font("Segoe UI Semibold", 10.5f, GraphicsUnit.Pixel);
            fIcon = new Font(Theme.IconFont, 16f, GraphicsUnit.Pixel);
            fIconS = new Font(Theme.IconFont, 11f, GraphicsUnit.Pixel);
            fIconL = new Font(Theme.IconFont, 28f, GraphicsUnit.Pixel);

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            tbHost = MakeBox(false); tbPort = MakeBox(false); tbPassword = MakeBox(true); tbAllow = MakeBox(false); tbSearch = MakeBox(false);
            tbSearch.TextChanged += (s, e) => { scroll = 0; Invalidate(); };
            tbAudioTag = MakeBox(true); tbAudD = MakeBox(true);
            tbAudioTag.Text = settings.AudioTagKey; tbAudD.Text = settings.AudDKey;
            foreach (var kb in new[] { tbAudioTag, tbAudD })
            {
                kb.Leave += (s, e) => SaveKeys();
                kb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveKeys(); ActiveControl = null; } };
            }
            tbDelay = MakeBox(false);
            tbDelay.TextAlign = HorizontalAlignment.Center;
            tbDelay.Text = FormatSeconds(settings.RestoreDelay);
            tbDelay.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != '.' && e.KeyChar != ',') e.Handled = true; };
            tbDelay.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitDelay(); ActiveControl = null; } };
            tbDelay.Leave += (s, e) => CommitDelay();
            tbDelay.Enter += (s, e) => Invalidate();
            tbSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { tbSearch.Text = ""; e.SuppressKeyPress = true; } };
            tbSearch.HandleCreated += (s, e) => SendMessageStr(tbSearch.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, "Search sources…");
            tbAllow.HandleCreated += (s, e) => SendMessageStr(tbAllow.Handle, 0x1501, (IntPtr)1, "Artist, Artist - Song, or label: Name");
            tbAudioTag.HandleCreated += (s, e) => SendMessageStr(tbAudioTag.Handle, 0x1501, (IntPtr)1, "Paste your AudioTag API key");
            tbAudD.HandleCreated += (s, e) => SendMessageStr(tbAudD.Handle, 0x1501, (IntPtr)1, "Paste your AudD API token");
            tbHost.Text = settings.Host; tbPort.Text = settings.Port.ToString(); tbPassword.Text = settings.Password;
            tbAllow.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddAllow(); } };
            foreach (var tb in new[] { tbHost, tbPort, tbPassword })
                tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Connect(); } };

            anim = new Timer { Interval = 16 };
            anim.Tick += OnAnim;
            secondTick = new Timer { Interval = 1000 };
            secondTick.Tick += (s, e) =>
            {
                if (Visible && WindowState != FormWindowState.Minimized && page == Page.Live && engine.Media.Any(m => m.State == PlayState.Playing)) Invalidate();
            };
            secondTick.Start();

            trayMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
            tray = new NotifyIcon { Text = "SongSentry", Visible = true, ContextMenuStrip = trayMenu };
            try { tray.Icon = Theme.MakeIcon(SystemInformation.SmallIconSize.Width); } catch { tray.Icon = Icon; }
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            BuildTrayMenu();   // built once up front: an empty menu doesn't open on the first right-click
            trayMenu.Opening += (s, e) => { trayPause.Text = settings.Paused ? "Resume protection" : "Pause protection"; };

            engine.Updated += QueueRepaint;
            engine.Notify += OnNotify;
            obs.StateChanged += QueueRepaint;
        }

        TextBox MakeBox(bool password)
        {
            var tb = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.Raised, ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 13.5f * scale, GraphicsUnit.Pixel), Visible = false,
                UseSystemPasswordChar = password
            };
            Controls.Add(tb);
            return tb;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x20000;       // WS_MINIMIZEBOX
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        public Page CurrentPage { get { return page; } set { page = value; scroll = 0; LayoutBoxes(); Invalidate(); } }

        // ================================================================== repaint plumbing

        void QueueRepaint()
        {
            if (!IsHandleCreated || repaintQueued) return;
            repaintQueued = true;
            try { BeginInvoke((Action)(() => { repaintQueued = false; if (Visible && WindowState != FormWindowState.Minimized) { Invalidate(); UpdateAnim(); } UpdateTray(); })); }
            catch { repaintQueued = false; }
        }

        bool Protecting { get { return engine.Channels.Any(c => c.Status == ChannelStatus.Protecting || c.Status == ChannelStatus.Warning); } }

        void UpdateAnim()
        {
            bool need = knobs.Any(k => Math.Abs(k.Value - KnobTarget(k.Key)) > 0.01f) || (Protecting && Visible && page == Page.Live);
            if (need && !anim.Enabled) anim.Start();
        }

        float KnobTarget(string id)
        {
            if (id == "pause") return settings.Paused ? 0f : 1f;
            if (id == "opt:tray") return settings.CloseToTray ? 1f : 0f;
            if (id == "opt:hidden") return settings.StartHidden ? 1f : 0f;
            if (id == "opt:skip") return settings.AutoSkip ? 1f : 0f;
            if (id == "opt:listen") return settings.ListenToAudio ? 1f : 0f;
            if (id == "opt:acoustid") return settings.UseAcoustId ? 1f : 0f;
            if (id.StartsWith("list:")) return settings.Lists.Enabled.Contains(id.Substring(5)) ? 1f : 0f;
            if (id.StartsWith("safeapp:")) return settings.IsSafeApp(id.Substring(8)) ? 1f : 0f;
            if (id == "opt:startwin") return startWithWindows ? 1f : 0f;
            var c = settings.Find(id.Substring(id.IndexOf(':') + 1));
            return c != null && c.Enabled ? 1f : 0f;
        }

        float Knob(string id)
        {
            float v;
            if (!knobs.TryGetValue(id, out v)) knobs[id] = v = KnobTarget(id);
            return v;
        }

        void OnAnim(object sender, EventArgs e)
        {
            bool moving = false;
            foreach (string k in knobs.Keys.ToList())
            {
                float t = KnobTarget(k), v = knobs[k];
                if (Math.Abs(t - v) > 0.01f) { knobs[k] = v + (t - v) * 0.3f; moving = true; }
                else knobs[k] = t;
            }
            bool pulsing = Protecting && Visible && WindowState != FormWindowState.Minimized && page == Page.Live;
            if (pulsing) pulse = (pulse + 0.012f) % 1f;
            if (toast != null && DateTime.Now > toastUntil) toast = null;
            if (!moving && !pulsing && toast == null) anim.Stop();
            anim.Interval = moving ? 16 : 40;   // the pulse ring doesn't need 60 fps
            Invalidate();
        }

        void ShowToast(string s)
        {
            toast = s; toastUntil = DateTime.Now.AddSeconds(2.6);
            if (!anim.Enabled) anim.Start();
            Invalidate();
        }

        // ================================================================== painting

        protected override void OnPaint(PaintEventArgs e) { RenderTo(e.Graphics, scale); }

        public void RenderTo(Graphics g, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Theme.Bg);
            g.ScaleTransform(s, s);
            hits.Clear();

            DrawSidebar(g);
            DrawHeader(g);
            var st = g.Save();
            g.SetClip(new RectangleF(SideW, HeadH, W - SideW, H - HeadH));
            if (page == Page.Live) DrawLive(g);
            else if (page == Page.Channels) DrawChannels(g);
            else if (page == Page.Recognition) DrawRecognition(g);
            else if (page == Page.SafeMusic) DrawSafeMusic(g);
            else if (page == Page.Playlists) DrawPlaylists(g);
            else DrawSettings(g);
            g.Restore(st);
            DrawToast(g);
            using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, 0.5f, 0.5f, W - 1, H - 1);
        }

        void DrawSidebar(Graphics g)
        {
            using (var b = new SolidBrush(Theme.Side)) g.FillRectangle(b, 0, 0, SideW, H);
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, SideW, 0, SideW, H);

            Theme.DrawLogo(g, new RectangleF(20, 20, 30, 30));
            Theme.Text2(g, "SongSentry", fBrand, Theme.Text, new RectangleF(58, 18, 130, 22), StringAlignment.Near, StringAlignment.Center);
            var beta = new RectangleF(59, 40, 36, 15);
            Theme.Stroke(g, beta, 7.5f, Theme.A(Theme.Accent, 160));
            Theme.Text2(g, "BETA", fCaps, Theme.Accent, beta, StringAlignment.Center, StringAlignment.Center);

            NavItem(g, Page.Live, Theme.GPulse, "Live", 96);
            NavItem(g, Page.Channels, Theme.GVolume, "Channels", 142);
            NavItem(g, Page.Recognition, Theme.GMusic, "Recognition", 188);
            NavItem(g, Page.SafeMusic, Theme.GCheck, "Safe music", 234);
            NavItem(g, Page.Playlists, Theme.GList, "Playlists", 280);
            NavItem(g, Page.Settings, Theme.GSettings, "Settings", 326);

            // OBS connection card
            var card = new RectangleF(14, H - 118, SideW - 28, 102);
            Theme.Fill(g, card, 10, Theme.Panel);
            Theme.Stroke(g, card, 10, Theme.Line);
            Color dot; string head, sub;
            ObsStatus(out dot, out head, out sub);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, card.X + 14, card.Y + 17, 8, 8);
            Theme.Text2(g, head, fBodyB, Theme.Text, new RectangleF(card.X + 28, card.Y + 10, card.Width - 36, 22), StringAlignment.Near, StringAlignment.Center);
            Theme.Wrap(g, sub, fTiny, Theme.Sub, new RectangleF(card.X + 14, card.Y + 34, card.Width - 24, 32));
            if (obs.State == ObsState.Connected)
            {
                bool live = engine.Live;
                var pill = new RectangleF(card.X + 14, card.Y + 72, live ? 44 : 58, 18);
                Theme.Fill(g, pill, 9, live ? Theme.Coral : Theme.Raised);
                Theme.Text2(g, live ? "LIVE" : "OFFLINE", fCaps, live ? Color.White : Theme.Sub, pill, StringAlignment.Center, StringAlignment.Center);
            }
            else
            {
                var btn = new RectangleF(card.X + 14, card.Y + 70, 90, 22);
                bool hot = hover == "retry";
                Theme.Fill(g, btn, 11, hot ? Theme.Hover : Theme.Raised);
                Theme.Text2(g, obs.State == ObsState.AuthFailed ? "Set password" : "Retry now", fSmall, Theme.Text, btn, StringAlignment.Center, StringAlignment.Center);
                Hit("retry", btn);
            }
        }

        void ObsStatus(out Color dot, out string head, out string sub)
        {
            switch (obs.State)
            {
                case ObsState.Connected:
                    dot = Theme.Accent; head = "OBS connected";
                    sub = "obs-websocket " + (obs.ObsVersion ?? "") + " · " + engine.TrackInfo;
                    break;
                case ObsState.AuthFailed:
                    dot = Theme.Amber; head = "Wrong password";
                    sub = "Check the OBS password in Settings.";
                    break;
                case ObsState.Connecting:
                    dot = Theme.Blue; head = "Connecting…";
                    sub = settings.Host + ":" + settings.Port;
                    break;
                default:
                    dot = Theme.Coral; head = "OBS not connected";
                    sub = obs.StateDetail ?? "Start OBS and enable Tools → WebSocket Server Settings.";
                    break;
            }
        }

        void NavItem(Graphics g, Page p, string glyph, string label, float y)
        {
            var r = new RectangleF(12, y, SideW - 24, 38);
            string id = "nav:" + p;
            bool sel = page == p, hot = hover == id;
            if (sel || hot) Theme.Fill(g, r, 9, sel ? Theme.Raised : Theme.Panel);
            if (sel) Theme.Fill(g, new RectangleF(r.X, r.Y + 9, 3, 20), 1.5f, Theme.Accent);
            Theme.Text2(g, glyph, fIcon, sel ? Theme.Accent : Theme.Sub, new RectangleF(r.X + 14, r.Y, 22, r.Height), StringAlignment.Center, StringAlignment.Center);
            Theme.Text2(g, label, sel ? fBodyB : fBody, sel ? Theme.Text : Theme.Sub, new RectangleF(r.X + 46, r.Y, 120, r.Height), StringAlignment.Near, StringAlignment.Center);
            if (p == Page.Live && Protecting)
                using (var b = new SolidBrush(Theme.Coral)) g.FillEllipse(b, r.Right - 22, r.Y + 15, 8, 8);
            Hit(id, r);
        }

        void DrawHeader(Graphics g)
        {
            string title, sub;
            switch (page)
            {
                case Page.Live: title = "Live"; sub = "What's playing, and what SongSentry is doing about it."; break;
                case Page.Channels: title = "Channels"; sub = "Pick the OBS sources to protect. Only switched-on sources are ever changed."; break;
                case Page.Recognition: title = "Recognition"; sub = "How SongSentry recognises songs in your sources' audio."; break;
                case Page.SafeMusic: title = "Safe music"; sub = "Which songs count as risky, and which are always fine to play."; break;
                case Page.Playlists: title = "Safe playlists"; sub = "Every song in these playlists counts as safe. Add, name and delete whole playlists."; break;
                default: title = "Settings"; sub = "OBS connection, your allow list, and behaviour."; break;
            }
            Theme.Text2(g, title, fH1, Theme.Text, new RectangleF(X0, 22, 400, 32), StringAlignment.Near, StringAlignment.Center);
            Theme.Text2(g, sub, fSmall, Theme.Sub, new RectangleF(X0, 54, CW - 60, 20), StringAlignment.Near, StringAlignment.Center);

            var rMin = new RectangleF(W - 92, 1, 46, 34);
            var rClose = new RectangleF(W - 46, 1, 45, 34);
            if (hover == "min") Theme.Fill(g, rMin, 0, Theme.Hover);
            if (hover == "close") Theme.Fill(g, rClose, 0, Color.FromArgb(232, 17, 35));
            Theme.Text2(g, Theme.GMin, fIconS, hover == "min" ? Theme.Text : Theme.Sub, rMin, StringAlignment.Center, StringAlignment.Center);
            Theme.Text2(g, Theme.GClose, fIconS, hover == "close" ? Color.White : Theme.Sub, rClose, StringAlignment.Center, StringAlignment.Center);
            Hit("min", rMin); Hit("close", rClose);
        }

        // ------------------------------------------------------------------ Live page

        void DrawLive(Graphics g)
        {
            var chans = engine.Channels;
            var enabled = settings.ChannelList().Where(c => c.Enabled).ToList();
            var acting = chans.Where(c => c.Status == ChannelStatus.Protecting || c.Status == ChannelStatus.Warning).ToList();

            // hero status
            var hero = new RectangleF(X0, HeadH + 4, CW, 118);
            Color tone; string head, sub, glyph = null;
            if (obs.State != ObsState.Connected) { tone = Theme.Dim; head = "Waiting for OBS"; sub = "SongSentry connects on its own once OBS is running with its WebSocket server on."; }
            else if (settings.Paused) { tone = Theme.Amber; head = "Protection paused"; sub = "Nothing is being changed in OBS. Resume when you're ready."; }
            else if (enabled.Count == 0) { tone = Theme.Dim; head = "No channels switched on"; sub = "Go to Channels and switch on the OBS sources that play music."; }
            else if (acting.Count > 0)
            {
                bool warnOnly = acting.All(a => a.Status == ChannelStatus.Warning);
                tone = warnOnly ? Theme.Amber : Theme.Coral;
                head = warnOnly ? "Licensed music is playing" : "Protecting your stream";
                var a0 = acting[0];
                sub = a0.Input + ": " + (a0.Detail ?? "") + (a0.Media != null ? "  ·  " + Engine.SongText(a0.Media) + LeftText(a0.Media, "  ·  ") : "") + (acting.Count > 1 ? "  (+" + (acting.Count - 1) + " more)" : "");
                glyph = warnOnly ? Theme.GWarn : Theme.GMute;
            }
            else { tone = Theme.Accent; head = "Guarding " + enabled.Count + (enabled.Count == 1 ? " channel" : " channels"); sub = "Nothing risky is playing right now."; }

            Theme.Fill(g, hero, 14, Theme.Panel);
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(hero.X - 60, hero.Y - 80, 360, 280);
                using (var pb = new PathGradientBrush(gp) { CenterColor = Theme.A(tone, 44), SurroundColors = new[] { Theme.A(tone, 0) } })
                {
                    var st = g.Save();
                    using (var clip = Theme.Round(hero, 14)) g.SetClip(clip, CombineMode.Intersect);
                    g.FillPath(pb, gp);
                    g.Restore(st);
                }
            }
            Theme.Stroke(g, hero, 14, Theme.Line);
            var shield = new RectangleF(hero.X + 26, hero.Y + 22, 64, 72);
            if (acting.Count > 0)
            {
                float k = pulse;
                var ring = RectangleF.Inflate(shield, 6 + k * 18, 6 + k * 18);
                using (var pen = new Pen(Theme.A(tone, (int)(120 * (1 - k))), 2f)) g.DrawEllipse(pen, ring);
            }
            using (var sp = Theme.ShieldPath(shield))
            using (var b = new LinearGradientBrush(shield, Theme.Mix(tone, Color.White, 0.25f), Theme.Mix(tone, Color.Black, 0.25f), 70f))
                g.FillPath(b, sp);
            if (glyph != null)
                Theme.Text2(g, glyph, fIconL, Color.White, new RectangleF(shield.X, shield.Y + 2, shield.Width, shield.Height - 8), StringAlignment.Center, StringAlignment.Center);
            else
                Theme.DrawNote(g, new RectangleF(shield.X + 18, shield.Y + 18, 29, 29), Color.White);

            Theme.Text2(g, head, fH1, Theme.Text, new RectangleF(hero.X + 116, hero.Y + 26, hero.Width - 300, 32), StringAlignment.Near, StringAlignment.Center);
            Theme.Wrap(g, sub, fBody, Theme.Sub, new RectangleF(hero.X + 117, hero.Y + 62, hero.Width - 320, 40));

            // pause / resume switch, or a shortcut to Channels
            if (obs.State == ObsState.Connected && enabled.Count == 0)
                Button(g, "goto:channels", "Set up channels", hero.Right - 170, hero.Y + 42, 146, true);
            else
            {
                Theme.Text2(g, settings.Paused ? "Paused" : "Protection on", fSmall, Theme.Sub, new RectangleF(hero.Right - 190, hero.Y + 46, 110, 24), StringAlignment.Far, StringAlignment.Center);
                Switch(g, "pause", hero.Right - 72, hero.Y + 46, Theme.Accent);
            }

            // now playing
            float y = hero.Bottom + 26;
            Caps(g, "NOW PLAYING", X0, y);
            y += 22;
            var media = engine.Media.OrderBy(m => m.State == PlayState.Playing ? 0 : 1).Take(3).ToList();
            if (media.Count == 0)
            {
                var empty = new RectangleF(X0, y, CW, 54);
                Theme.Stroke(g, empty, 10, Theme.Line);
                Theme.Text2(g, nowPlaying.Available ? "Nothing is reporting to Windows' media controls right now (Spotify, browsers, …)." : "Windows media info isn't available on this PC.",
                            fSmall, Theme.Dim, empty, StringAlignment.Center, StringAlignment.Center);
                y = empty.Bottom;
            }
            foreach (var m in media)
            {
                var card = new RectangleF(X0, y, CW, 64);
                Theme.Fill(g, card, 10, Theme.Panel);
                AppBadge(g, m.App, new RectangleF(card.X + 14, card.Y + 14, 36, 36));
                Theme.Text2(g, string.IsNullOrEmpty(m.Title) ? "(no title)" : m.Title, fBodyB, Theme.Text, new RectangleF(card.X + 64, card.Y + 12, card.Width - 400, 20), StringAlignment.Near, StringAlignment.Center);
                string meta = (string.IsNullOrEmpty(m.Artist) ? "" : m.Artist + "  ·  ") + AppKey.Pretty(m.App) + "  ·  " + (m.State == PlayState.Playing ? "playing" : m.State == PlayState.Paused ? "paused" : "stopped") + LeftText(m, "  ·  ");
                Theme.Text2(g, meta, fSmall, Theme.Sub, new RectangleF(card.X + 64, card.Y + 33, card.Width - 400, 18), StringAlignment.Near, StringAlignment.Center);

                var ch = settings.ChannelList().FirstOrDefault(c => c.Apps.Contains(m.App) && c.Enabled);
                var view = ch != null ? chans.FirstOrDefault(v => v.Input == ch.Input) : null;
                float rx = card.Right - 16;
                if (ch == null)
                {
                    float w = LinkText(g, "link:" + m.App, "Link to a channel…", rx, card.Y + 22, true);
                    Theme.Text2(g, "Not protected", fSmall, Theme.Dim, new RectangleF(rx - w - 130, card.Y + 22, 120, 20), StringAlignment.Far, StringAlignment.Center);
                }
                else
                {
                    Chip(g, view, rx, card.Y + 12, true);
                    if (m.State == PlayState.Playing && view != null && view.Status != ChannelStatus.Allowed)
                        LinkText(g, "safe:" + ch.Input, "Mark as safe", rx, card.Y + 38, true);
                    Theme.Text2(g, "→ " + ch.Input, fTiny, Theme.Dim, new RectangleF(rx - 300, card.Y + 38, 180, 18), StringAlignment.Far, StringAlignment.Center);
                }
                y = card.Bottom + 8;
            }

            // activity
            y += 18;
            Caps(g, "ACTIVITY", X0, y);
            y += 22;
            var evs = engine.Events;
            if (evs.Count == 0) Theme.Text2(g, "Nothing yet. Actions and restores will show up here.", fSmall, Theme.Dim, new RectangleF(X0, y, CW, 20), StringAlignment.Near, StringAlignment.Center);
            foreach (var ev in evs)
            {
                if (y > H - 30) break;
                Theme.Text2(g, ev.Time.ToString("HH:mm:ss"), fSmall, Theme.Dim, new RectangleF(X0, y, 60, 20), StringAlignment.Near, StringAlignment.Center);
                using (var b = new SolidBrush(ev.Alert ? Theme.Coral : Theme.Accent)) g.FillEllipse(b, X0 + 66, y + 7, 6, 6);
                Theme.Text2(g, ev.Channel, fSmall, Theme.Text, new RectangleF(X0 + 80, y, 140, 20), StringAlignment.Near, StringAlignment.Center);
                Theme.Text2(g, ev.Text + (ev.Song != null ? "  ·  " + ev.Song : ""), fSmall, Theme.Sub, new RectangleF(X0 + 226, y, CW - 226, 20), StringAlignment.Near, StringAlignment.Center);
                y += 24;
            }
        }

        static string LeftText(MediaInfo m, string prefix)
        {
            TimeSpan? left = m == null || m.State == PlayState.Stopped ? null : m.Remaining(DateTime.UtcNow);
            if (!left.HasValue || m.Duration.TotalSeconds < 5) return "";
            return prefix + (int)left.Value.TotalMinutes + ":" + left.Value.Seconds.ToString("00") + " left";
        }

        void AppBadge(Graphics g, string app, RectangleF r)
        {
            Color c = app == "spotify" ? Color.FromArgb(30, 215, 96) : AppKey.IsBrowser(app) ? Theme.Blue : Theme.Amber;
            Theme.Fill(g, r, r.Width / 2, Theme.A(c, 38));
            string glyph = AppKey.IsBrowser(app) ? Theme.GGlobe : Theme.GMusic;
            Theme.Text2(g, glyph, fIcon, c, r, StringAlignment.Center, StringAlignment.Center);
        }

        /// Status chip for a channel, right-aligned at x. Returns its width.
        float Chip(Graphics g, ChannelView v, float right, float y, bool alignRight)
        {
            Color c; string text;
            ChipStyle(v, out c, out text);
            SizeF sz = g.MeasureString(text, fCaps);
            var r = new RectangleF(alignRight ? right - sz.Width - 18 : right, y, sz.Width + 18, 20);
            Theme.Fill(g, r, 10, Theme.A(c, 34));
            Theme.Text2(g, text, fCaps, c, r, StringAlignment.Center, StringAlignment.Center);
            return r.Width;
        }

        static void ChipStyle(ChannelView v, out Color c, out string text)
        {
            if (v == null) { c = Theme.Dim; text = "OFF"; return; }
            switch (v.Status)
            {
                case ChannelStatus.Protecting: c = Theme.Coral; text = (v.Detail ?? "PROTECTING").ToUpperInvariant(); break;
                case ChannelStatus.Warning: c = Theme.Amber; text = "WARNING: LICENSED MUSIC"; break;
                case ChannelStatus.Allowed: c = Theme.Accent; text = "ALLOWED"; break;
                case ChannelStatus.Checking: c = Theme.Blue; text = "CHECKING SONG…"; break;
                case ChannelStatus.Overridden: c = Theme.Amber; text = "LEFT ALONE (MANUAL)"; break;
                case ChannelStatus.Listening: c = Theme.Accent; text = v.Detail == "Not a known song" ? "NOT A KNOWN SONG" : "GUARDING"; break;
                case ChannelStatus.Idle: c = Theme.Accent; text = "GUARDING"; break;
                case ChannelStatus.NoObs: c = Theme.Dim; text = (v.Detail ?? "WAITING").ToUpperInvariant(); break;
                default: c = Theme.Dim; text = v.Detail == "Paused" ? "PAUSED" : "OFF"; break;
            }
        }

        // ------------------------------------------------------------------ Channels page

        void DrawChannels(Graphics g)
        {
            float top = HeadH + 4;
            if (obs.State != ObsState.Connected)
            {
                EmptyState(g, top, "Connect to OBS to see your audio sources.", "Settings → OBS connection", "nav:Settings");
                return;
            }
            var inputs = engine.Inputs;
            var hiddenSet = new HashSet<string>(settings.HiddenList());
            // A protected source is never hidden, even if it's on the hidden list.
            Func<InputInfo, bool> inUse = i => { var c = settings.Find(i.Name); return c != null && c.Enabled; };
            Func<InputInfo, bool> isHidden = i => hiddenSet.Contains(i.Name) && !inUse(i);

            // row 1: search box, sort, refresh
            Field(g, tbSearch, null, X0, top - 18, 260);
            Theme.Text2(g, "\uE721", fIconS, Theme.Dim, new RectangleF(X0 + 236, top + 8, 16, 18), StringAlignment.Center, StringAlignment.Center);
            string sortLabel = sortByName ? "Sort: Name A–Z" : "Sort: Suggested";
            var sortR = new RectangleF(X0 + 272, top, 150, 34);
            Theme.Fill(g, sortR, 8, hover == "sort" ? Theme.Hover : Theme.Raised);
            Theme.Text2(g, sortLabel, fSmall, Theme.Text, new RectangleF(sortR.X + 12, sortR.Y, sortR.Width - 34, sortR.Height), StringAlignment.Near, StringAlignment.Center);
            Theme.Text2(g, Theme.GChevron, fIconS, Theme.Dim, new RectangleF(sortR.Right - 24, sortR.Y + 1, 14, sortR.Height), StringAlignment.Center, StringAlignment.Center);
            Hit("sort", sortR);
            Theme.Text2(g, engine.TrackInfo, fTiny, Theme.Dim, new RectangleF(X0 + 434, top, CW - 520, 34), StringAlignment.Near, StringAlignment.Center);
            LinkText(g, "refresh", "Refresh", X0 + CW, top + 7, true);

            // row 2: filter chips with counts
            string q = tbSearch.Text.Trim();
            Func<InputInfo, bool> matches = i => q.Length == 0
                || i.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (i.Target ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
            var searched = inputs.Where(matches).ToList();
            var visibleSet = searched.Where(i => showHidden || !isHidden(i)).ToList();
            float cx = X0, cy = top + 44;
            foreach (var f in Filters)
            {
                int n = visibleSet.Count(i => InFilter(f.Key, i, inUse));
                cx = FilterChip(g, "filter:" + f.Key, f.Value, n, filter == f.Key, cx, cy) + 8;
            }
            int hiddenCount = searched.Count(isHidden);
            if (hiddenCount > 0 || showHidden)
            {
                cx += 6;
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, cx, cy + 5, cx, cy + 23);
                cx = FilterChip(g, "showhidden", "Hidden", hiddenCount, showHidden, cx + 12, cy, Theme.GView);
            }

            float listTop = top + 84, listBottom = H - 16;
            var list = visibleSet.Where(i => InFilter(filter, i, inUse));
            var ordered = (sortByName
                ? list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : list.OrderBy(i => isHidden(i) ? 1 : 0).ThenBy(i => inUse(i) ? 0 : 1).ThenBy(i => MusicLikely(i) ? 0 : 1)
                      .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
            const float rowH = 66;
            scrollMax = Math.Max(0, ordered.Count * (rowH + 8) - 8 - (listBottom - listTop));
            scroll = Math.Max(0, Math.Min(scroll, scrollMax));
            var st = g.Save();
            g.SetClip(new RectangleF(SideW + 1, listTop, W - SideW - 2, listBottom - listTop), CombineMode.Intersect);
            var views = engine.Channels;
            float y = listTop - scroll;
            foreach (var i in ordered)
            {
                if (y + rowH >= listTop && y <= listBottom)
                    DrawChannelRow(g, i, views.FirstOrDefault(v => v.Input == i.Name), new RectangleF(X0, y, CW - 16, rowH), listTop, listBottom, isHidden(i));
                y += rowH + 8;
            }
            if (ordered.Count == 0)
            {
                string why = q.Length > 0 ? "No sources match \"" + q + "\"" + (filter != "all" ? " in this filter." : ".")
                           : filter != "all" ? "No sources in this filter." : "All sources are hidden. Turn on the Hidden filter to see them.";
                Theme.Text2(g, why, fBody, Theme.Sub, new RectangleF(X0, listTop + 40, CW, 24), StringAlignment.Center, StringAlignment.Center);
                if (q.Length > 0 || filter != "all")
                {
                    SizeF sz = g.MeasureString("Clear search and filters", fSmall);
                    LinkText(g, "clearfilters", "Clear search and filters", X0 + CW / 2 - sz.Width / 2, listTop + 70, false);
                }
            }            g.Restore(st);

            Scrollbar(g, W - 30, listTop, listBottom);
        }

        /// Scrollbar for a list scrolled by `scroll`/`scrollMax`: drag the thumb, or click the track to jump a page.
        void Scrollbar(Graphics g, float x, float top, float bottom)
        {
            trackRect = RectangleF.Empty;
            if (scrollMax <= 0) return;
            trackRect = new RectangleF(x, top, 14, bottom - top);
            float thumbH = Math.Max(40, trackRect.Height * trackRect.Height / (trackRect.Height + scrollMax));
            float ty = top + (trackRect.Height - thumbH) * (scroll / scrollMax);
            thumbRect = new RectangleF(trackRect.X, ty, trackRect.Width, thumbH);
            bool hot = dragging || hover == "scrollthumb" || hover == "scrolltrack";
            if (hot) Theme.Fill(g, new RectangleF(x + 5, top, 6, trackRect.Height), 3, Theme.A(Theme.Hover, 90));
            float bw = hot ? 6 : 4;
            Theme.Fill(g, new RectangleF(x + 8 - bw / 2 + (hot ? 0 : 1), ty, bw, thumbH), bw / 2, dragging ? Theme.Sub : hot ? Theme.Dim : Theme.Hover);
            Hit("scrolltrack", trackRect);
            Hit("scrollthumb", thumbRect);
        }

        bool showHidden, dragging, suppressClick, sortByName;
        string filter = "all";

        static readonly KeyValuePair<string, string>[] Filters =
        {
            new KeyValuePair<string, string>("all", "All"),
            new KeyValuePair<string, string>("inuse", "In use"),
            new KeyValuePair<string, string>("perapp", "Per-app"),
            new KeyValuePair<string, string>("devices", "Devices"),
            new KeyValuePair<string, string>("media", "Media & other"),
            new KeyValuePair<string, string>("shared", "Shared"),
        };

        static bool InFilter(string f, InputInfo i, Func<InputInfo, bool> inUse)
        {
            bool device = i.Kind == "wasapi_input_capture" || i.Kind == "wasapi_output_capture";
            switch (f)
            {
                case "inuse": return inUse(i);
                case "perapp": return i.Kind == "wasapi_process_output_capture";
                case "devices": return device;
                case "media": return !device && i.Kind != "wasapi_process_output_capture";
                case "shared":   // carries several apps (worth splitting), or is the whole desktop mix
                    return (i.Kind == "wasapi_output_capture" && (i.DeviceId == null || i.DeviceId == "default")) || Engine.Carried(i).Count >= 2;
                default: return true;
            }
        }

        float FilterChip(Graphics g, string id, string label, int count, bool sel, float x, float y, string glyph = null)
        {
            string text = label + "  " + count;
            SizeF sz = g.MeasureString(text, fSmall);
            float w = sz.Width + 20 + (glyph != null ? 18 : 0);
            var r = new RectangleF(x, y, w, 28);
            bool hot = hover == id;
            if (sel) Theme.Fill(g, r, 14, Theme.A(Theme.Accent, 40));
            else Theme.Fill(g, r, 14, hot ? Theme.Hover : Theme.Raised);
            if (sel) Theme.Stroke(g, r, 14, Theme.A(Theme.Accent, 150));
            float tx = r.X + 10;
            if (glyph != null)
            {
                Theme.Text2(g, glyph, fIconS, sel ? Theme.Accent : Theme.Sub, new RectangleF(tx, r.Y, 14, r.Height), StringAlignment.Center, StringAlignment.Center);
                tx += 18;
            }
            SizeF ls = g.MeasureString(label, fSmall);
            Theme.Text2(g, label, fSmall, sel ? Theme.Text : Theme.Sub, new RectangleF(tx, r.Y, ls.Width + 2, r.Height), StringAlignment.Near, StringAlignment.Center);
            Theme.Text2(g, count.ToString(), fSmall, sel ? Theme.Accent : Theme.Dim, new RectangleF(tx + ls.Width, r.Y, r.Right - tx - ls.Width - 8, r.Height), StringAlignment.Near, StringAlignment.Center);
            Hit(id, r);
            return r.Right;
        }
        float dragStartY, dragStartScroll;
        RectangleF trackRect, thumbRect;

        static bool MusicLikely(InputInfo i)
        {
            string n = (i.Name + " " + i.Target).ToLowerInvariant();
            return i.App != null || n.Contains("music") || n.Contains("spotify") || n.Contains("browser") || n.Contains("desktop") || n.Contains("sampler");
        }

        void DrawChannelRow(Graphics g, InputInfo i, ChannelView v, RectangleF r, float clipTop, float clipBottom, bool hidden)
        {
            var c = settings.Find(i.Name);
            bool on = c != null && c.Enabled;
            string id = i.Name;
            bool visible = r.Bottom > clipTop && r.Y < clipBottom;
            if (visible) Hit("row:" + id, new RectangleF(r.X, Math.Max(r.Y, clipTop), r.Width, Math.Min(r.Bottom, clipBottom) - Math.Max(r.Y, clipTop)));
            bool hot = hover != null && hover.EndsWith(":" + id);
            Theme.Fill(g, r, 12, on ? Theme.Panel : (hot ? Theme.Panel : Theme.A(Theme.Panel, hidden ? 90 : 150)));
            if (on) Theme.Stroke(g, r, 12, Theme.A(Theme.Accent, 60));
            if (hidden) Theme.Stroke(g, r, 12, Theme.A(Theme.Line, 200));

            Switch(g, "toggle:" + id, r.X + 18, r.Y + 21, Theme.Accent, clipTop, clipBottom);

            // hide / unhide button (only for sources that aren't protected)
            if (!on && (hot || hidden))
            {
                var hb = new RectangleF(r.X + 300, r.Y + 19, 30, 28);
                string hid = (hidden ? "unhide:" : "hide:") + id;
                bool hh = hover == hid;
                if (hh) Theme.Fill(g, hb, 8, Theme.Hover);
                Color ic = hh ? Theme.Text : Theme.Dim;
                Theme.Text2(g, Theme.GView, fIcon, ic, hb, StringAlignment.Center, StringAlignment.Center);
                if (!hidden)
                    using (var pen = new Pen(ic, 1.6f)) g.DrawLine(pen, hb.X + 8, hb.Y + 21, hb.Right - 8, hb.Y + 7);
                if (visible) Hit(hid, hb);
            }

            Theme.Text2(g, i.Name, fBodyB, on ? Theme.Text : (hidden ? Theme.Dim : Theme.Sub), new RectangleF(r.X + 82, r.Y + 12, 212, 20), StringAlignment.Near, StringAlignment.Center);
            string line2;
            Color c2 = Theme.Dim;
            if (on && v != null && v.Status != ChannelStatus.Off && v.Status != ChannelStatus.Idle)
            {
                string t; ChipStyle(v, out c2, out t);
                line2 = v.Status == ChannelStatus.Protecting || v.Status == ChannelStatus.Warning || v.Status == ChannelStatus.Allowed
                        ? (v.Detail ?? "") + (v.Media != null ? " · " + Engine.SongText(v.Media) : "") : (v.Detail ?? i.Target);
            }
            else line2 = i.Target + (i.Muted ? "  ·  muted in OBS" : "");
            Channel shown = c ?? engine.NewChannel(i.Name, i);
            if (i.Kind == "wasapi_output_capture" && (i.DeviceId == null || i.DeviceId == "default") && !on)
            {
                line2 = "Default output (all desktop sound, muting it silences everything)";
                c2 = Theme.Amber;
            }
            else if (on && (v == null || v.Status == ChannelStatus.Idle || v.Status == ChannelStatus.Listening) && shown.Action != ActionKind.Warn
                     && !Engine.Carried(i).Any(a => !shown.Apps.Contains(a)))
            {
                string rs = settings.ListenToAudio ? recognizer.StateOf(i.Name) : "Audio recognition is off (Recognition page)";
                if (rs != null) { line2 = rs; c2 = rs.StartsWith("Listening") ? Theme.Accent : Theme.Amber; }
            }
            else if (on && (v == null || v.Status == ChannelStatus.Idle || v.Status == ChannelStatus.Listening) && shown.Action != ActionKind.Warn)
            {
                // Other apps share this source: muting it silences them too.
                var others = Engine.Carried(i).Where(a => !shown.Apps.Contains(a)).Select(AppKey.Pretty).ToList();
                if (others.Count > 0)
                {
                    line2 = "Also carries " + string.Join(", ", others.Take(3)) + (others.Count > 3 ? "…" : "") +
                            (shown.Action == ActionKind.Duck ? ": they get turned down too" : ": muting silences them too");
                    c2 = Theme.Amber;
                }
            }
            Theme.Text2(g, line2, fSmall, c2, new RectangleF(r.X + 82, r.Y + 34, (!on && (hot || hidden)) ? 212 : 256, 18), StringAlignment.Near, StringAlignment.Center);

            // "Listens to" and "Action" dropdown pills
            string appLabel = shown.Apps.Count == 0 ? "Choose apps…" : shown.AppsLabel;
            bool allBrowsers = shown.Apps.Count > 0 && shown.Apps.All(AppKey.IsBrowser);
            Pill(g, "app:" + id, "LISTENS TO", allBrowsers ? Theme.GGlobe : Theme.GMusic, appLabel, r.X + 350, r.Y + 12, 150, on, clipTop, clipBottom);
            ActionKind act = c != null ? c.Action : ActionKind.StreamOnly;
            Pill(g, "action:" + id, "WHEN A SONG PLAYS", act == ActionKind.Warn ? Theme.GWarn : Theme.GMute,
                 Channel.ActionLabel(act, c != null ? c.DuckPercent : 20), r.X + 512, r.Y + 12, r.Width - 530, on, clipTop, clipBottom);
        }

        void Pill(Graphics g, string id, string label, string glyph, string value, float x, float y, float w, bool on, float clipTop, float clipBottom)
        {
            Theme.Text2(g, label, fCaps, Theme.Dim, new RectangleF(x + 2, y - 2, w, 14), StringAlignment.Near, StringAlignment.Center);
            var r = new RectangleF(x, y + 14, w, 28);
            bool hot = hover == id;
            Theme.Fill(g, r, 8, hot ? Theme.Hover : Theme.Raised);
            Theme.Text2(g, glyph, fIconS, on ? Theme.Accent : Theme.Sub, new RectangleF(r.X + 8, r.Y, 16, r.Height), StringAlignment.Center, StringAlignment.Center);
            Theme.Text2(g, value, fSmall, on ? Theme.Text : Theme.Sub, new RectangleF(r.X + 28, r.Y, r.Width - 48, r.Height), StringAlignment.Near, StringAlignment.Center);
            Theme.Text2(g, Theme.GChevron, fIconS, Theme.Dim, new RectangleF(r.Right - 22, r.Y + 1, 14, r.Height), StringAlignment.Center, StringAlignment.Center);
            if (r.Bottom > clipTop && r.Y < clipBottom) Hit(id, r);
        }

        // ------------------------------------------------------------------ Settings page

        readonly Dictionary<TextBox, RectangleF> boxRects = new Dictionary<TextBox, RectangleF>();

        void DrawSettings(Graphics g)
        {
            float y = HeadH + 4, colW = (CW - 24) / 2;
            // OBS connection
            var card = new RectangleF(X0, y, colW, 262);
            Section(g, card, "OBS CONNECTION");
            Field(g, tbHost, "Host", card.X + 18, card.Y + 44, colW - 150);
            Field(g, tbPort, "Port", card.Right - 118, card.Y + 44, 100);
            Field(g, tbPassword, "Password", card.X + 18, card.Y + 106, colW - 36);
            float bx = Button(g, "connect", "Save & connect", card.X + 18, card.Y + 170, 0, true);
            Button(g, "obscfg", "Load from OBS", bx + 10, card.Y + 170, 0, false);
            Theme.Wrap(g, "Connects automatically on start and whenever OBS opens. Turn it on in OBS: Tools → WebSocket Server Settings.",
                       fTiny, Theme.Dim, new RectangleF(card.X + 18, card.Y + 210, colW - 36, 48));

            // app
            var app = new RectangleF(X0, card.Bottom + 16, colW, H - card.Bottom - 32);
            Section(g, app, "APP");
            float ay = app.Y + 42;
            OptionRow(g, "opt:startwin", "Start with Windows (in the tray)", ay, app);
            OptionRow(g, "opt:tray", "Keep running in the tray when closed", ay + 42, app);
            OptionRow(g, "opt:hidden", "Start hidden in the tray", ay + 84, app);

            // song handling
            float rx = X0 + colW + 24;
            var songs = new RectangleF(rx, y, colW, 262);
            Section(g, songs, "SONG HANDLING");
            Theme.Text2(g, "Give a source back", fBodyB, Theme.Text, new RectangleF(songs.X + 18, songs.Y + 38, colW - 36, 22), StringAlignment.Near, StringAlignment.Center);
            bool quiet = settings.Restore == RestoreMode.AfterQuiet;
            Radio(g, "restore:quiet", quiet, songs.X + 18, songs.Y + 70);
            Theme.Text2(g, "After", fBody, quiet ? Theme.Text : Theme.Sub, new RectangleF(songs.X + 44, songs.Y + 64, 40, 28), StringAlignment.Near, StringAlignment.Center);
            SmallField(g, tbDelay, songs.X + 88, songs.Y + 63, 56);
            Theme.Text2(g, "seconds of quiet", fBody, quiet ? Theme.Text : Theme.Sub, new RectangleF(songs.X + 152, songs.Y + 64, 160, 28), StringAlignment.Near, StringAlignment.Center);
            Hit("restore:quiet", new RectangleF(songs.X + 12, songs.Y + 64, 72, 28));
            Radio(g, "restore:end", !quiet, songs.X + 18, songs.Y + 108);
            Theme.Text2(g, "When the track is over", fBody, !quiet ? Theme.Text : Theme.Sub, new RectangleF(songs.X + 44, songs.Y + 102, colW - 70, 28), StringAlignment.Near, StringAlignment.Center);
            Hit("restore:end", new RectangleF(songs.X + 12, songs.Y + 102, 220, 28));
            Theme.Wrap(g, "Spotify, YouTube Music, browsers: stays muted while that song is paused, and shows the time left.",
                       fTiny, Theme.Dim, new RectangleF(songs.X + 44, songs.Y + 128, colW - 62, 32));
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, songs.X + 18, songs.Y + 168, songs.Right - 18, songs.Y + 168);
            OptionRow(g, "opt:skip", "Skip to the next track", songs.Y + 180, songs);
            Theme.Wrap(g, "Presses \"next\" in the player when a licensed song starts (muted meanwhile). Stops after 5 skips in a row.",
                       fTiny, Theme.Dim, new RectangleF(songs.X + 18, songs.Y + 210, colW - 36, 32));

            // allow list
            var al = new RectangleF(rx, songs.Bottom + 16, colW, H - songs.Bottom - 32);
            Section(g, al, "ALLOW LIST  ·  NEVER MUTED");
            Field(g, tbAllow, null, al.X + 18, al.Y + 22, colW - 110);
            Button(g, "allowadd", "Add", al.Right - 82, al.Y + 40, 64, true);
            float ly = al.Y + 82;
            var allow = settings.AllowList();
            if (allow.Count == 0)
                Theme.Wrap(g, "Artists or songs you may play (StreamBeats, Pretzel, NCS, your own music). Or press \"Mark as safe\" on the Live page.",
                           fTiny, Theme.Dim, new RectangleF(al.X + 18, ly, colW - 36, 40));
            int shown = 0;
            for (int k = 0; k < allow.Count && ly < al.Bottom - 30; k++, shown++)
            {
                var row = new RectangleF(al.X + 12, ly, colW - 24, 26);
                string id = "allowdel:" + k;
                if (hover == id) Theme.Fill(g, row, 8, Theme.Raised);
                string e = allow[k];
                bool artist = e.StartsWith("artist:"), label = e.StartsWith("label:");
                Theme.Text2(g, artist ? Theme.GMic : label ? Theme.GInfo : Theme.GMusic, fIconS, Theme.Accent, new RectangleF(row.X + 6, row.Y, 18, row.Height), StringAlignment.Center, StringAlignment.Center);
                Theme.Text2(g, e.Substring(e.IndexOf(':') + 1), fBody, Theme.Text, new RectangleF(row.X + 30, row.Y, row.Width - 110, row.Height), StringAlignment.Near, StringAlignment.Center);
                Theme.Text2(g, artist ? "artist" : label ? "label" : "song", fTiny, Theme.Dim, new RectangleF(row.Right - 90, row.Y, 50, row.Height), StringAlignment.Far, StringAlignment.Center);
                Theme.Text2(g, Theme.GCancel, fIconS, hover == id ? Theme.Coral : Theme.Dim, new RectangleF(row.Right - 30, row.Y, 24, row.Height), StringAlignment.Center, StringAlignment.Center);
                Hit(id, new RectangleF(row.Right - 34, row.Y, 32, row.Height));
                ly += 28;
            }
            if (shown < allow.Count)
                Theme.Text2(g, "+ " + (allow.Count - shown) + " more", fTiny, Theme.Dim, new RectangleF(al.X + 18, al.Bottom - 24, 120, 16), StringAlignment.Near, StringAlignment.Center);
            Theme.Text2(g, "SongSentry " + Program.Version + " beta", fTiny, Theme.Dim, new RectangleF(al.X, al.Bottom - 24, al.Width - 18, 16), StringAlignment.Far, StringAlignment.Center);
        }

        // ------------------------------------------------------------------ Recognition page

        void DrawRecognition(Graphics g)
        {
            float y = HeadH + 4, colW = (CW - 24) / 2;

            // listening
            var lis = new RectangleF(X0, y, colW, 170);
            Section(g, lis, "LISTEN TO THE AUDIO");
            OptionRow(g, "opt:listen", "Recognise songs in protected sources", lis.Y + 40, lis);
            Theme.Wrap(g, "Listens to each switched-on source (per app, or its sound device) and checks it against the song memory every " +
                          "2 seconds, offline. Works mid-song and under loud game sound. Browser and media-file sources use Now Playing only.",
                       fTiny, Theme.Dim, new RectangleF(lis.X + 18, lis.Y + 76, colW - 36, 80));

            // online services
            var on = new RectangleF(X0, lis.Bottom + 16, colW, H - lis.Bottom - 32);
            Section(g, on, "ONLINE FALLBACKS  ·  EACH ONE TRIES WHEN THE LAST FAILS");
            Theme.Wrap(g, "For music the song memory doesn't know yet. Every song they name is learned, so it's only looked up once.",
                       fTiny, Theme.Dim, new RectangleF(on.X + 18, on.Y + 34, colW - 36, 32));
            OptionRow(g, "opt:acoustid", "AcoustID  ·  free, no key needed", on.Y + 64, on);
            KeyField(g, tbAudioTag, "AudioTag  ·  free", "url:audiotag", "Get a free key", on.X + 18, on.Y + 110, colW - 36);
            KeyField(g, tbAudD, "AudD  ·  best in game noise", "url:audd", "Get a key (300 free)", on.X + 18, on.Y + 180, colW - 36);
            Theme.Wrap(g, "Only a fingerprint (AcoustID) or a short clip (AudioTag, AudD) of unknown music is sent; keys stay encrypted here. " +
                          "Includes Chromaprint fpcalc (LGPL 2.1).", fTiny, Theme.Dim, new RectangleF(on.X + 18, on.Bottom - 58, colW - 36, 50));

            // song memory
            float rx = X0 + colW + 24;
            var mem = new RectangleF(rx, y, colW, H - y - 16);
            Section(g, mem, "SONG MEMORY");
            var lib = recognizer.Library;
            Theme.Text2(g, lib.Count + (lib.Count == 1 ? " song" : " songs") + " learned  ·  " + (lib.SizeBytes / 1024 >= 1024 ? (lib.SizeBytes / 1048576.0).ToString("0.0") + " MB" : lib.SizeBytes / 1024 + " KB"),
                        fBodyB, Theme.Text, new RectangleF(mem.X + 18, mem.Y + 38, colW - 150, 22), StringAlignment.Near, StringAlignment.Center);
            if (lib.Count > 0) LinkText(g, "mem:clear", "Clear", mem.Right - 18, mem.Y + 40, true);
            float ly = mem.Y + 70;
            var songs = lib.Songs();
            if (songs.Count == 0)
                Theme.Wrap(g, "Empty for now. SongSentry learns every song it identifies (Now Playing, AudioTag, AudD) while it plays, " +
                              "and from then on recognises it offline, anywhere, even from the middle.", fSmall, Theme.Dim, new RectangleF(mem.X + 18, ly, colW - 36, 90));
            foreach (var s in songs)
            {
                if (ly > mem.Bottom - 40) { Theme.Text2(g, "+ " + (songs.Count - songs.IndexOf(s)) + " more", fTiny, Theme.Dim, new RectangleF(mem.X + 18, ly, 120, 16), StringAlignment.Near, StringAlignment.Center); break; }
                bool safe = s.Safe || settings.IsAllowed(s.Artist, s.Title, s.Label);
                Theme.Text2(g, safe ? Theme.GCheck : Theme.GMusic, fIconS, safe ? Theme.Accent : Theme.Coral, new RectangleF(mem.X + 18, ly, 16, 34), StringAlignment.Center, StringAlignment.Center);
                Theme.Text2(g, s.Text, fBody, Theme.Text, new RectangleF(mem.X + 42, ly, colW - 60, 18), StringAlignment.Near, StringAlignment.Center);
                string meta = (safe ? "safe" : "licensed") + "  ·  " + s.Source + (string.IsNullOrEmpty(s.Label) ? "" : "  ·  " + s.Label) + "  ·  " +
                              (int)(s.Track.Frames * LandmarkIndex.FrameSec / 60) + ":" + ((int)(s.Track.Frames * LandmarkIndex.FrameSec) % 60).ToString("00");
                Theme.Text2(g, meta, fTiny, Theme.Dim, new RectangleF(mem.X + 42, ly + 17, colW - 60, 16), StringAlignment.Near, StringAlignment.Center);
                ly += 40;
            }
        }

        void KeyField(Graphics g, TextBox tb, string label, string urlId, string urlText, float x, float y, float w)
        {
            Theme.Text2(g, label, fSmall, Theme.Sub, new RectangleF(x, y, w - 140, 16), StringAlignment.Near, StringAlignment.Center);
            LinkText(g, urlId, urlText, x + w, y - 1, true);
            var r = new RectangleF(x, y + 20, w, 34);
            Theme.Fill(g, r, 8, Theme.Raised);
            Theme.Stroke(g, r, 8, tb.Focused ? Theme.Accent : Theme.Line);
            boxRects[tb] = new RectangleF(r.X + 10, r.Y + 8, r.Width - 20, r.Height - 14);
            bool set = !string.IsNullOrWhiteSpace(tb == tbAudD ? settings.AudDKey : settings.AudioTagKey);
            if (set && !tb.Focused)
                Theme.Text2(g, Theme.GCheck, fIconS, Theme.Accent, new RectangleF(r.Right - 26, r.Y, 18, r.Height), StringAlignment.Center, StringAlignment.Center);
        }

        void SaveKeys()
        {
            string at = tbAudioTag.Text.Trim(), ad = tbAudD.Text.Trim();
            if (at == settings.AudioTagKey && ad == settings.AudDKey) return;
            settings.AudioTagKey = at; settings.AudDKey = ad;
            settings.Save();
            ShowToast("Keys saved (encrypted)");
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------------ Safe music page

        static Color RiskColor(RiskLevel l)
        {
            switch (l) { case RiskLevel.Major: return Theme.Coral; case RiskLevel.Independent: return Theme.Amber; case RiskLevel.Safe: return Theme.Accent; default: return Theme.Blue; }
        }

        static string ActionName(RiskAction a) { return a == RiskAction.Protect ? "Protect" : a == RiskAction.Warn ? "Warn only" : "Ignore"; }

        void DrawSafeMusic(Graphics g)
        {
            float y = HeadH + 4, colW = (CW - 24) / 2, rx = X0 + colW + 24;

            // risk levels
            var rk = new RectangleF(X0, y, colW, 262);
            Section(g, rk, "RISK LEVELS  ·  WHO RELEASED THE SONG");
            var levels = new[]
            {
                new KeyValuePair<RiskLevel, string>(RiskLevel.Major, "Universal, Sony, Warner & their labels: almost always claimed"),
                new KeyValuePair<RiskLevel, string>(RiskLevel.Independent, "Indie labels & self-released: often claimed via distributors"),
                new KeyValuePair<RiskLevel, string>(RiskLevel.Unknown, "Label not found (or still checking)"),
                new KeyValuePair<RiskLevel, string>(RiskLevel.Safe, "Creative Commons or on a stream-safe list"),
            };
            float ly = rk.Y + 40;
            foreach (var lv in levels)
            {
                using (var b = new SolidBrush(RiskColor(lv.Key))) g.FillEllipse(b, rk.X + 18, ly + 8, 9, 9);
                Theme.Text2(g, RiskInfo.LevelName(lv.Key), fBodyB, Theme.Text, new RectangleF(rk.X + 34, ly, 170, 24), StringAlignment.Near, StringAlignment.Center);
                Theme.Wrap(g, lv.Value, fTiny, Theme.Dim, new RectangleF(rk.X + 34, ly + 22, colW - 170, 30));
                var pill = new RectangleF(rk.Right - 130, ly + 6, 112, 28);
                string pid = "risk:" + lv.Key;
                RiskAction ra = settings.ActionFor(lv.Key);
                Theme.Fill(g, pill, 8, hover == pid ? Theme.Hover : Theme.Raised);
                Theme.Text2(g, ActionName(ra), fSmall, ra == RiskAction.Protect ? Theme.Coral : ra == RiskAction.Warn ? Theme.Amber : Theme.Sub,
                            new RectangleF(pill.X + 10, pill.Y, pill.Width - 30, pill.Height), StringAlignment.Near, StringAlignment.Center);
                Theme.Text2(g, Theme.GChevron, fIconS, Theme.Dim, new RectangleF(pill.Right - 22, pill.Y + 1, 14, pill.Height), StringAlignment.Center, StringAlignment.Center);
                Hit(pid, pill);
                ly += 54;
            }

            // safe apps
            var sa = new RectangleF(X0, rk.Bottom + 16, colW, H - rk.Bottom - 32);
            Section(g, sa, "SAFE APPS  ·  EVERYTHING THEY PLAY IS FINE");
            var apps = new List<string>();
            foreach (var md in engine.Media) if (!apps.Contains(md.App)) apps.Add(md.App);
            foreach (string a in settings.AllowList().Where(x => x.StartsWith("app:")).Select(x => x.Substring(4))) if (!apps.Contains(a)) apps.Add(a);
            foreach (string a in new[] { "pretzel", "spotify", "youtube music" }) if (!apps.Contains(a)) apps.Add(a);
            float ay = sa.Y + 38;
            foreach (string a in apps.Take(5))
            {
                Theme.Text2(g, AppKey.Pretty(a) + (engine.Media.Any(x => x.App == a) ? "" : ""), fBody, Theme.Text, new RectangleF(sa.X + 18, ay, colW - 100, 28), StringAlignment.Near, StringAlignment.Center);
                Switch(g, "safeapp:" + a, sa.Right - 66, ay + 2, Theme.Accent);
                ay += 34;
            }

            // stream-safe lists (built in; the streamer's own lists and playlists are on the Playlists page)
            var sl = new RectangleF(rx, y, colW, 300);
            Section(g, sl, "STREAM-SAFE LISTS");
            LinkText(g, "help:lists", "How it works", sl.Right - 18, sl.Y + 13, true);
            float sy = sl.Y + 36;
            foreach (var l in settings.Lists.BuiltIn)
            {
                Theme.Text2(g, l.Name, fBody, Theme.Text, new RectangleF(sl.X + 18, sy, colW - 130, 18), StringAlignment.Near, StringAlignment.Center);
                Theme.Text2(g, (l.Paid ? "PAID  ·  " : "") + l.Note, fTiny, l.Paid ? Theme.Amber : Theme.Dim, new RectangleF(sl.X + 18, sy + 17, colW - 130, 14), StringAlignment.Near, StringAlignment.Center);
                Switch(g, "list:" + l.Id, sl.Right - 66, sy + 5, Theme.Accent);
                sy += 31;
            }

            // the streamer's playlists (managed on the Playlists page)
            var pl = new RectangleF(rx, sl.Bottom + 16, colW, H - sl.Bottom - 32);
            Section(g, pl, "YOUR SAFE PLAYLISTS");
            var mine = settings.Lists.Mine;
            float py = pl.Y + 38;
            if (mine.Count == 0)
                Theme.Wrap(g, "Add Spotify or YouTube playlists, Pear's playlist, or scan the one that's playing. Every song in them counts as safe.",
                           fSmall, Theme.Sub, new RectangleF(pl.X + 18, py, colW - 36, 60));
            foreach (var l in mine.OrderByDescending(x => x.Added).Take(3))
            {
                bool on = settings.Lists.Enabled.Contains(l.Id);
                Theme.Text2(g, l.Name, fBody, on ? Theme.Text : Theme.Dim, new RectangleF(pl.X + 18, py, colW - 150, 22), StringAlignment.Near, StringAlignment.Center);
                Theme.Text2(g, CountText(l), fTiny, Theme.Dim, new RectangleF(pl.Right - 150, py, 132, 22), StringAlignment.Far, StringAlignment.Center);
                py += 26;
            }
            if (mine.Count > 3) Theme.Text2(g, "+ " + (mine.Count - 3) + " more", fTiny, Theme.Dim, new RectangleF(pl.X + 18, py, 120, 18), StringAlignment.Near, StringAlignment.Center);
            Button(g, "nav:Playlists", mine.Count == 0 ? "Add a playlist" : "Manage playlists", pl.X + 18, pl.Bottom - 50, 0, mine.Count == 0);
        }

        static string CountText(SafeList l)
        {
            if (!l.Playlist && l.Labels.Count + l.Artists.Count > 0) return l.Count + (l.Count == 1 ? " entry" : " entries");
            if (l.Total > l.Tracks.Count) return l.Tracks.Count + " of " + l.Total + " songs";
            return l.Tracks.Count.ToString("N0") + (l.Tracks.Count == 1 ? " song" : " songs");
        }

        // ------------------------------------------------------------------ Playlists page

        void DrawPlaylists(Graphics g)
        {
            float y = HeadH + 4;
            var add = new RectangleF(X0, y, CW, 162);
            Section(g, add, "ADD A SAFE PLAYLIST");
            float hw = LinkText(g, "help:playlists", "How it works", add.Right - 18, add.Y + 13, true);
            LinkText(g, "pl:file", "Import file…", add.Right - 18 - hw - 18, add.Y + 13, true);
            float cw3 = (CW - 36 - 40) / 3f, cx = add.X + 18, top = add.Y + 38;

            Theme.Text2(g, "Spotify or YouTube", fBodyB, Theme.Text, new RectangleF(cx, top, cw3, 20), StringAlignment.Near, StringAlignment.Center);
            Theme.Wrap(g, "Paste a playlist link, or songs copied from Spotify.", fTiny, Theme.Dim, new RectangleF(cx, top + 22, cw3, 32));
            Button(g, "pl:add", plBusy ? "Reading…" : "Add playlist…", cx, top + 58, 0, true);

            cx += cw3 + 20;
            Theme.Text2(g, "Pear Desktop", fBodyB, Theme.Text, new RectangleF(cx, top, cw3, 20), StringAlignment.Near, StringAlignment.Center);
            Theme.Wrap(g, "Reads the playlist playing in Pear, all at once.", fTiny, Theme.Dim, new RectangleF(cx, top + 22, cw3, 32));
            Button(g, "pear:go", "Read Pear's playlist", cx, top + 58, 0, false);

            cx += cw3 + 20;
            Theme.Text2(g, "Any player", fBodyB, Theme.Text, new RectangleF(cx, top, cw3, 20), StringAlignment.Near, StringAlignment.Center);
            Theme.Wrap(g, "Plays through the playlist that's playing, ~2 s a song.", fTiny, Theme.Dim, new RectangleF(cx, top + 22, cw3, 32));
            if (scanApp == null) scanApp = engine.Media.Where(x => x.State == PlayState.Playing).Select(x => x.App).FirstOrDefault() ?? engine.Media.Select(x => x.App).FirstOrDefault();
            var ap = new RectangleF(cx, top + 59, cw3 - 84, 30);
            Theme.Fill(g, ap, 8, hover == "scan:app" ? Theme.Hover : Theme.Raised);
            Theme.Text2(g, scanApp == null ? "No player" : AppKey.Pretty(scanApp), fSmall, Theme.Text, new RectangleF(ap.X + 10, ap.Y, ap.Width - 30, ap.Height), StringAlignment.Near, StringAlignment.Center);
            Theme.Text2(g, Theme.GChevron, fIconS, Theme.Dim, new RectangleF(ap.Right - 22, ap.Y + 1, 14, ap.Height), StringAlignment.Center, StringAlignment.Center);
            Hit("scan:app", ap);
            Button(g, "scan:go", scanner.Running ? "Stop" : "Scan", ap.Right + 8, top + 58, 76, false);

            string st = scanner.Running ? scanner.Status : plStatus;
            if (!string.IsNullOrEmpty(st))
                Theme.Text2(g, st, fTiny, scanner.Running || plBusy ? Theme.Accent : Theme.Sub, new RectangleF(add.X + 18, add.Bottom - 26, CW - 36, 16), StringAlignment.Near, StringAlignment.Center);

            var ls = new RectangleF(X0, add.Bottom + 16, CW, H - add.Bottom - 32);
            Section(g, ls, "YOUR SAFE PLAYLISTS");
            var mine = settings.Lists.Mine.OrderByDescending(x => x.Added).ToList();
            int songs = mine.Sum(l => l.Count);
            Theme.Text2(g, mine.Count + (mine.Count == 1 ? " playlist" : " playlists") + "  ·  " + songs.ToString("N0") + " songs", fSmall, Theme.Sub,
                        new RectangleF(ls.Right - 318, ls.Y + 10, 300, 22), StringAlignment.Far, StringAlignment.Center);
            float listTop = ls.Y + 40, listBottom = ls.Bottom - 8;
            const float rowH = 52;
            scrollMax = Math.Max(0, mine.Count * rowH - (listBottom - listTop));
            scroll = Math.Max(0, Math.Min(scroll, scrollMax));
            if (mine.Count == 0)
                Theme.Text2(g, "No playlists yet. Add one above: every song in it will count as safe.", fBody, Theme.Sub,
                            new RectangleF(ls.X, listTop + 40, ls.Width, 24), StringAlignment.Center, StringAlignment.Center);
            var saved = g.Save();
            g.SetClip(new RectangleF(ls.X + 1, listTop, ls.Width - 2, listBottom - listTop), CombineMode.Intersect);
            float ry = listTop - scroll;
            foreach (var l in mine)
            {
                if (ry + rowH >= listTop && ry <= listBottom) DrawPlaylistRow(g, l, new RectangleF(ls.X + 18, ry, ls.Width - 52, rowH), listTop, listBottom);
                ry += rowH;
            }
            g.Restore(saved);
            Scrollbar(g, ls.Right - 24, listTop, listBottom);
        }

        void DrawPlaylistRow(Graphics g, SafeList l, RectangleF r, float clipTop, float clipBottom)
        {
            bool on = settings.Lists.Enabled.Contains(l.Id);
            if (hover != null && PlaylistIdAt(hover) == l.Id) Theme.Fill(g, new RectangleF(r.X - 10, r.Y + 2, r.Width + 20, r.Height - 4), 8, Theme.A(Theme.Raised, 170));
            if (r.Bottom > clipTop && r.Y < clipBottom) Hit("plrow:" + l.Id, new RectangleF(r.X - 10, Math.Max(r.Y, clipTop), r.Width + 20, Math.Min(r.Bottom, clipBottom) - Math.Max(r.Y, clipTop)));   // right-click anywhere on it
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.X, r.Bottom - 0.5f, r.Right, r.Bottom - 0.5f);
            Theme.Text2(g, Theme.GList, fIconS, on ? Theme.Accent : Theme.Dim, new RectangleF(r.X, r.Y, 20, r.Height), StringAlignment.Center, StringAlignment.Center);
            Theme.Text2(g, l.Name, fBodyB, on ? Theme.Text : Theme.Sub, new RectangleF(r.X + 30, r.Y + 7, r.Width - 190, 20), StringAlignment.Near, StringAlignment.Center);
            string meta = CountText(l) + (l.Source != null ? "  ·  " + l.Source : "") + (l.Added != DateTime.MinValue ? "  ·  added " + l.Added.ToString("d MMM") : "")
                        + (l.Url != null ? "  ·  updates on start" : "");
            Theme.Text2(g, meta, fTiny, l.Total > l.Tracks.Count ? Theme.Amber : Theme.Dim, new RectangleF(r.X + 30, r.Y + 27, r.Width - 190, 16), StringAlignment.Near, StringAlignment.Center);
            Switch(g, "list:" + l.Id, r.Right - 128, r.Y + 14, Theme.Accent, clipTop, clipBottom);
            var more = new RectangleF(r.Right - 70, r.Y + 12, 28, 28);
            var del = new RectangleF(r.Right - 34, r.Y + 12, 28, 28);
            if (hover == "plmenu:" + l.Id) Theme.Fill(g, more, 7, Theme.Hover);
            Theme.Text2(g, "•••", fSmall, Theme.Sub, more, StringAlignment.Center, StringAlignment.Center);
            Theme.Text2(g, Theme.GCancel, fIconS, hover == "pldel:" + l.Id ? Theme.Coral : Theme.Dim, del, StringAlignment.Center, StringAlignment.Center);
            if (more.Bottom > clipTop && more.Y < clipBottom) { Hit("plmenu:" + l.Id, more); Hit("pldel:" + l.Id, del); }
        }

        void ShowRiskMenu(RiskLevel level, Point at)
        {
            var m = NewMenu();
            RiskAction cur = settings.ActionFor(level);
            foreach (RiskAction a in new[] { RiskAction.Protect, RiskAction.Warn, RiskAction.Ignore })
            {
                RiskAction act = a;
                string text = a == RiskAction.Protect ? "Protect  (use the channel's action)" : a == RiskAction.Warn ? "Warn only  (never changes OBS)" : "Ignore  (let it play)";
                Add(m, text, cur == a, () =>
                {
                    if (level == RiskLevel.Major) settings.RiskMajor = act; else if (level == RiskLevel.Independent) settings.RiskIndependent = act;
                    else if (level == RiskLevel.Unknown) settings.RiskUnknown = act; else settings.RiskSafe = act;
                    settings.Save(); engine.Reevaluate(); Invalidate();
                    ShowToast(RiskInfo.LevelName(level) + ": " + ActionName(act));
                });
            }
            m.Show(this, at);
        }

        void ImportFile()
        {
            using (var d = new OpenFileDialog { Filter = "Song lists (*.txt;*.csv)|*.txt;*.csv|All files|*.*", Title = "Import a playlist or stream-safe list" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var l = settings.Lists.ImportFile(d.FileName); settings.Save(); engine.Reevaluate();
                    plStatus = "Imported \"" + l.Name + "\": " + CountText(l) + "."; ShowToast(plStatus);
                }
                catch (Exception ex) { ShowToast("Couldn't import: " + ex.Message); }
            }
        }

        void ShowHelp(string title, string text)
        {
            MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowScanAppMenu(Point at)
        {
            var m = NewMenu();
            var apps = engine.Media.Select(x => x.App).Distinct().ToList();
            if (apps.Count == 0) { ShowToast("Open your music player first"); return; }
            foreach (string a in apps) { string k = a; Add(m, AppKey.Pretty(a), a == scanApp, () => { scanApp = k; Invalidate(); }); }
            m.Show(this, at);
        }

        void StartOrStopScan(string presetName)
        {
            if (scanner.Running) { scanner.Stop(); return; }
            if (scanApp == null) { ShowToast("Open your music player and start the playlist first"); return; }
            string n = presetName ?? settings.Lists.NextAutoName("My Playlist"), none = null;
            if (!AskPlaylist("Scan the playlist that's playing",
                    "SongSentry will press \"next\" in " + AppKey.Pretty(scanApp) + " every couple of seconds and save each song, until the playlist starts over " +
                    "(about 2 s per song).\n\nStart your stream-safe playlist first with shuffle off, and mute the player if you like.",
                    false, ref none, ref n, "Save the songs as (a new name, or pick a playlist to add to):", true) || n.Length == 0) return;
            int added;
            var l = settings.Lists.SavePlaylist(n, "Scan · " + AppKey.Pretty(scanApp), null, new string[0], 0, out added);
            scanTarget = l.Id;
            scanner.Known = l.Tracks.Count > 0 ? new HashSet<string>(l.Tracks.Select(SafeLists.Key)) : null;
            engine.ScanningApp = scanApp;
            scanner.Start(scanApp);
            Invalidate();
        }

        void ScanFound(string entry)
        {
            string id = scanTarget;
            if (id == null) return;
            settings.Lists.AddTracks(id, new[] { entry.StartsWith("track:") ? entry.Substring(6) : entry });
            engine.Reevaluate();
        }

        void ScanFinished()
        {
            engine.ScanningApp = null;
            var l = scanTarget != null ? settings.Lists.ById(scanTarget) : null;
            if (l != null && l.Tracks.Count == 0) settings.Lists.Remove(l.Id);   // nothing found: don't keep an empty playlist
            plStatus = scanner.Status + (l != null && l.Tracks.Count > 0 ? "  Saved in \"" + l.Name + "\" (" + CountText(l) + ")." : "");
            scanTarget = null;
            settings.Save();
            engine.Reevaluate();
        }

        void ReadPear()
        {
            if (plBusy) return;
            plBusy = true; plStatus = "Asking Pear Desktop… if Pear shows a message, click Allow."; Invalidate();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                PlaylistRead r = null; string err = null;
                try
                {
                    string token = settings.PearToken, pid;
                    var songs = PearDesktop.Queue(settings.PearPort, ref token, out pid);
                    settings.PearToken = token; settings.Save();
                    if (songs.Count == 0) throw new InvalidOperationException("Pear's queue is empty. Play the playlist in Pear first.");
                    r = new PlaylistRead { Source = "Pear Desktop", Tracks = songs, Total = songs.Count };
                    if (pid != null && !pid.StartsWith("RD"))
                        try
                        {
                            // Pear said which playlist this is: take its name (and any songs past the queue) from YouTube Music
                            var yt = PlaylistLinks.Read("https://music.youtube.com/playlist?list=" + pid, null);
                            r.Name = yt.Name; r.Url = yt.Url;
                            foreach (string t in yt.Tracks) if (!r.Tracks.Contains(t)) r.Tracks.Add(t);
                            r.Total = Math.Max(r.Tracks.Count, yt.Total);
                        }
                        catch { }   // a private playlist: ask for a name instead
                }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => { plBusy = false; if (err != null) { plStatus = err; ShowToast(err); } else SaveRead(r, null); Invalidate(); })); } catch { }
            });
        }

        void AddPlaylist(string presetName)
        {
            if (plBusy) return;
            string text = "", name = presetName ?? "";
            if (!AskPlaylist("Add a safe playlist",
                    "Paste a Spotify, YouTube or YouTube Music playlist link.\n\nBig or private Spotify playlist? Links only share the first 100 songs. " +
                    "Open the playlist in the Spotify app, click a song, press Ctrl+A then Ctrl+C, and paste here.\n\n" +
                    "Heads-up: Spotify limits how fast songs can be looked up, so pasted Spotify songs can be really slow " +
                    "(several minutes for a big playlist). Keep SongSentry open until it says the playlist is saved.",
                    true, ref text, ref name, "Name (optional). Empty = the playlist's own name, or \"My Playlist #1\". Pick one to add the songs to it:", true)) return;
            text = text.Trim();
            if (text.Length == 0) return;
            plBusy = true; plStatus = "Reading…"; Invalidate();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                PlaylistRead r = null; SafeList list = null; string err = null, url = null;
                try
                {
                    if (PlaylistLinks.Recognizes(text)) r = PlaylistLinks.Read(text, pr => { plStatus = pr; QueueRepaint(); });
                    else
                    {
                        // a link to a text/CSV list, or pasted "Artist - Title" lines
                        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^https?://\S+$")) url = text;
                        list = SafeLists.Parse(url != null ? SafeLists.Download(url) : text, url != null ? Uri.UnescapeDataString(new Uri(url).Segments.Last().Trim('/')) : null);
                        if (list.Count == 0) throw new InvalidOperationException("No songs found. Paste a Spotify or YouTube playlist link, or songs copied from Spotify.");
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        plBusy = false;
                        if (err != null) { plStatus = err; MessageBox.Show(this, err, "Add a safe playlist", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                        else if (list != null && !list.Playlist)
                        {
                            // labels / artists: a stream-safe list rather than a playlist
                            string n = name.Length > 0 ? name : list.Name ?? "";
                            if (n.Length == 0) n = settings.Lists.NextAutoName("My List");
                            list.Name = n;
                            settings.Lists.AddParsed(list, url); settings.Save(); engine.Reevaluate();
                            plStatus = "Added \"" + n + "\": " + CountText(list) + "."; ShowToast(plStatus);
                        }
                        else
                        {
                            if (list != null) r = new PlaylistRead { Name = list.Name, Source = url != null ? "Link" : "Pasted", Url = url, Tracks = list.Tracks, Total = list.Tracks.Count };
                            SaveRead(r, name);
                        }
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        /// Saves what was read under the given name, the playlist's own name, or a name the streamer types.
        void SaveRead(PlaylistRead r, string name)
        {
            string n = !string.IsNullOrWhiteSpace(name) ? name.Trim() : r.Name;
            bool auto = string.IsNullOrWhiteSpace(n);
            if (auto) n = settings.Lists.NextAutoName("My Playlist");   // no name from the source: "My Playlist #1", "#2"...
            int added;
            var l = settings.Lists.SavePlaylist(n, r.Source, r.Url, r.Tracks, r.Total, out added);
            settings.Save(); engine.Reevaluate();
            string msg = added == l.Tracks.Count ? "Saved \"" + l.Name + "\": " + CountText(l)
                                                 : "Added " + added + " new songs to \"" + l.Name + "\" (" + CountText(l) + ")";
            if (r.Failed > 0) msg += ". " + r.Failed + " couldn't be read";
            plStatus = msg + "." + (auto ? "  Right-click it to rename." : ""); ShowToast(msg);
            if (r.Source == "Spotify" && r.Total > r.Tracks.Count)
                MessageBox.Show(this, "Spotify links only share the first " + r.Tracks.Count + " of this playlist's " + r.Total + " songs.\n\n" +
                                      "To add the rest: open the playlist in the Spotify app, click any song, press Ctrl+A then Ctrl+C. " +
                                      "Then click Add playlist… again, paste, and pick \"" + l.Name + "\" as the name so the songs join this playlist.",
                                "Only part of the playlist", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// The playlist under a hit id (its row, switch, ••• or ×), or null.
        string PlaylistIdAt(string hit)
        {
            if (hit == null) return null;
            foreach (string p in new[] { "plrow:", "plmenu:", "pldel:", "list:" })
                if (hit.StartsWith(p)) { string id = hit.Substring(p.Length); return settings.Lists.ById(id) != null ? id : null; }
            return null;
        }

        void ShowPlaylistMenu(string id, Point at)
        {
            var l = settings.Lists.ById(id);
            if (l == null) return;
            var m = NewMenu();
            Add(m, "Rename…", false, () =>
            {
                string n = l.Name, none = null;
                if (AskPlaylist("Rename", "New name for \"" + l.Name + "\":", false, ref none, ref n, "Name:", false) && n.Length > 0) { settings.Lists.Rename(l.Id, n); settings.Save(); Invalidate(); }
            });
            Add(m, "Show songs…", false, () => ShowSongs(l));
            Add(m, "Add songs (a link or copied songs)…", false, () => AddPlaylist(l.Name));
            Add(m, "Add songs by scanning the playing playlist…", false, () => StartOrStopScan(l.Name));
            if (PlaylistLinks.IsPlaylistLink(l.Url)) Add(m, "Check the link for new songs now", false, () => RefreshPlaylist(l));
            m.Items.Add(new ToolStripSeparator());
            Add(m, "Delete playlist", false, () => DeletePlaylist(l.Id));
            m.Show(this, at);
        }

        void RefreshPlaylist(SafeList l)
        {
            plBusy = true; plStatus = "Checking \"" + l.Name + "\"…"; Invalidate();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg;
                try
                {
                    var r = PlaylistLinks.Read(l.Url, null);
                    int added = settings.Lists.AddTracks(l.Id, r.Tracks);
                    l.Total = Math.Max(l.Total, r.Total);
                    settings.Save(); engine.Reevaluate();
                    msg = added == 0 ? "\"" + l.Name + "\" is up to date." : "Added " + added + " new songs to \"" + l.Name + "\".";
                }
                catch (Exception ex) { msg = ex.Message; }
                try { BeginInvoke((Action)(() => { plBusy = false; plStatus = msg; ShowToast(msg); Invalidate(); })); } catch { }
            });
        }

        void DeletePlaylist(string id)
        {
            var l = settings.Lists.ById(id);
            if (l == null) return;
            if (MessageBox.Show(this, "Delete \"" + l.Name + "\" and its " + CountText(l) + "?\n\nThey'll no longer count as safe (unless they're in another playlist or list).",
                                "Delete playlist", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            settings.Lists.Remove(id); settings.Save(); engine.Reevaluate();
            plStatus = "Deleted \"" + l.Name + "\"."; ShowToast(plStatus); Invalidate();
        }

        void ShowSongs(SafeList l)
        {
            var lines = l.Labels.Select(x => "label: " + x).Concat(l.Artists.Select(x => "artist: " + x)).Concat(l.Tracks);
            using (var f = DialogForm(l.Name + "  ·  " + CountText(l), 560, 460))
            {
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                                             BackColor = Theme.Raised, ForeColor = Theme.Text, Text = string.Join("\r\n", lines) });
                f.ShowDialog(this);
            }
        }

        Form DialogForm(string title, int w, int h)
        {
            return new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Segoe UI", 9.5f),
                AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96f, 96f), ClientSize = new Size(w, h), Icon = Icon
            };
        }

        /// A small dialog: an optional paste box, then a name box that also offers the existing playlists (pick one to add to it).
        bool AskPlaylist(string title, string intro, bool paste, ref string text, ref string name, string nameLabel, bool listExisting)
        {
            using (var f = DialogForm(title, 520, 100))
            {
                int y = 16, w = 488;
                var font = f.Font;
                var lab = new Label { Text = intro, Left = 16, Top = y, Width = w, ForeColor = Theme.Sub };
                lab.Height = TextRenderer.MeasureText(intro, font, new Size(w, 0), TextFormatFlags.WordBreak).Height + 4;
                f.Controls.Add(lab); y = lab.Bottom + 8;
                TextBox box = null;
                if (paste)
                {
                    box = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Left = 16, Top = y, Width = w, Height = 96,
                                        BackColor = Theme.Raised, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = text ?? "" };
                    f.Controls.Add(box); y = box.Bottom + 12;
                }
                var nl = new Label { Text = nameLabel, Left = 16, Top = y, Width = w, ForeColor = Theme.Sub };
                nl.Height = TextRenderer.MeasureText(nameLabel, font, new Size(w, 0), TextFormatFlags.WordBreak).Height + 2;
                f.Controls.Add(nl); y = nl.Bottom + 2;
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Left = 16, Top = y, Width = w, FlatStyle = FlatStyle.Flat,
                                        BackColor = Theme.Raised, ForeColor = Theme.Text, Text = name ?? "" };
                if (listExisting) foreach (var l in settings.Lists.Mine) cb.Items.Add(l.Name);
                f.Controls.Add(cb); y = cb.Bottom + 18;
                var ok = new Button { Text = paste ? "Add" : "Save", DialogResult = DialogResult.OK, Left = 16 + w - 196, Top = y, Width = 94, Height = 30,
                                      FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Theme.AccentInk };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 16 + w - 94, Top = y, Width = 94, Height = 30,
                                          FlatStyle = FlatStyle.Flat, BackColor = Theme.Raised, ForeColor = Theme.Text };
                ok.FlatAppearance.BorderSize = 0; cancel.FlatAppearance.BorderColor = Theme.Line;
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.ClientSize = new Size(520, y + 30 + 16);
                f.Shown += (s, e) => { if (box != null) box.Focus(); else { cb.Focus(); cb.SelectAll(); } };
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                if (box != null) text = box.Text;
                name = cb.Text.Trim();
                return true;
            }
        }

        void Radio(Graphics g, string id, bool on, float x, float y)
        {
            bool hot = hover == id;
            using (var pen = new Pen(on ? Theme.Accent : hot ? Theme.Sub : Theme.Dim, 1.6f)) g.DrawEllipse(pen, x, y, 16, 16);
            if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, x + 4, y + 4, 8, 8);
        }

        /// A compact text box (e.g. the seconds field).
        void SmallField(Graphics g, TextBox tb, float x, float y, float w)
        {
            var r = new RectangleF(x, y, w, 30);
            Theme.Fill(g, r, 7, Theme.Raised);
            Theme.Stroke(g, r, 7, tb.Focused ? Theme.Accent : Theme.Line);
            boxRects[tb] = new RectangleF(r.X + 8, r.Y + 6, r.Width - 16, r.Height - 11);
        }
        static string TitleCase(string s)
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s);
        }

        void Section(Graphics g, RectangleF r, string caps)
        {
            Theme.Fill(g, r, 14, Theme.Panel);
            Theme.Stroke(g, r, 14, Theme.Line);
            Caps(g, caps, r.X + 18, r.Y + 16);
        }

        void Field(Graphics g, TextBox tb, string label, float x, float y, float w)
        {
            Theme.Text2(g, label, fSmall, Theme.Sub, new RectangleF(x, y, w, 16), StringAlignment.Near, StringAlignment.Center);
            var r = new RectangleF(x, y + 18, w, 34);
            Theme.Fill(g, r, 8, Theme.Raised);
            Theme.Stroke(g, r, 8, tb.Focused ? Theme.Accent : Theme.Line);
            boxRects[tb] = new RectangleF(r.X + 10, r.Y + 8, r.Width - (tb == tbSearch ? 40 : 20), r.Height - 14);
        }

        void OptionRow(Graphics g, string id, string label, float y, RectangleF card)
        {
            Theme.Text2(g, label, fBody, Theme.Text, new RectangleF(card.X + 18, y, card.Width - 100, 28), StringAlignment.Near, StringAlignment.Center);
            Switch(g, id, card.Right - 66, y + 2, Theme.Accent);
        }

        void LayoutBoxes()
        {
            foreach (var tb in new[] { tbHost, tbPort, tbPassword, tbAllow, tbSearch, tbDelay, tbAudioTag, tbAudD })
            {
                bool show = tb == tbSearch ? page == Page.Channels && obs.State == ObsState.Connected
                          : tb == tbAudioTag || tb == tbAudD ? page == Page.Recognition : page == Page.Settings;
                RectangleF r;
                if (show && boxRects.TryGetValue(tb, out r))
                    tb.Bounds = new Rectangle((int)(r.X * scale), (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale));
                tb.Visible = show && boxRects.ContainsKey(tb);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        // ------------------------------------------------------------------ shared widgets

        void Switch(Graphics g, string id, float x, float y, Color onColor, float clipTop = 0, float clipBottom = 99999)
        {
            float k = Knob(id);
            var r = new RectangleF(x, y, 46, 24);
            bool hot = hover == id;
            Theme.Fill(g, r, 12, Theme.Mix(hot ? Theme.Hover : Theme.SwitchOff, hot ? Theme.AccentHi : onColor, k));
            float kx = r.X + 3 + k * (r.Width - 24);
            using (var b = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillEllipse(b, kx, r.Y + 4, 18, 18);
            using (var b = new SolidBrush(Theme.Mix(Theme.Sub, Color.White, k))) g.FillEllipse(b, kx, r.Y + 3, 18, 18);
            if (r.Bottom > clipTop && r.Y < clipBottom) Hit(id, RectangleF.Inflate(r, 4, 6));
        }

        float Button(Graphics g, string id, string text, float x, float y, float w, bool primary)
        {
            if (w <= 0) w = g.MeasureString(text, fBodyB).Width + 28;
            var r = new RectangleF(x, y, w, 32);
            bool hot = hover == id;
            Theme.Fill(g, r, 9, primary ? (hot ? Theme.AccentHi : Theme.Accent) : (hot ? Theme.Hover : Theme.Raised));
            Theme.Text2(g, text, fBodyB, primary ? Theme.AccentInk : Theme.Text, r, StringAlignment.Center, StringAlignment.Center);
            Hit(id, r);
            return r.Right;
        }

        /// Accent text link, right-aligned at x when alignRight. Returns its width.
        float LinkText(Graphics g, string id, string text, float x, float y, bool alignRight)
        {
            SizeF sz = g.MeasureString(text, fSmall);
            var r = new RectangleF(alignRight ? x - sz.Width : x, y, sz.Width, 20);
            Theme.Text2(g, text, fSmall, hover == id ? Theme.AccentHi : Theme.Accent, r, StringAlignment.Near, StringAlignment.Center);
            Hit(id, RectangleF.Inflate(r, 4, 3));
            return sz.Width;
        }

        void Caps(Graphics g, string text, float x, float y)
        {
            Theme.Text2(g, text, fCaps, Theme.Dim, new RectangleF(x, y, 400, 16), StringAlignment.Near, StringAlignment.Center);
        }

        void EmptyState(Graphics g, float top, string text, string link, string linkId)
        {
            var r = new RectangleF(X0, top + 40, CW, 160);
            Theme.Stroke(g, r, 14, Theme.Line);
            Theme.Text2(g, text, fBody, Theme.Sub, new RectangleF(r.X, r.Y + 50, r.Width, 24), StringAlignment.Center, StringAlignment.Center);
            SizeF sz = g.MeasureString(link, fSmall);
            LinkText(g, linkId, link, r.X + r.Width / 2 - sz.Width / 2, r.Y + 84, false);
        }

        void DrawToast(Graphics g)
        {
            if (toast == null) return;
            SizeF sz = g.MeasureString(toast, fSmall);
            float w = Math.Min(CW, sz.Width + 36);
            var r = new RectangleF(SideW + (W - SideW) / 2 - w / 2, H - 52, w, 34);
            Theme.Fill(g, r, 17, Theme.Hover);
            Theme.Stroke(g, r, 17, Theme.A(Theme.Accent, 90));
            Theme.Text2(g, toast, fSmall, Theme.Text, r, StringAlignment.Center, StringAlignment.Center);
        }

        void Hit(string id, RectangleF r) { hits.Add(new KeyValuePair<string, RectangleF>(id, r)); }

        // ================================================================== input

        string HitTest(Point p)
        {
            var q = new PointF(p.X / scale, p.Y / scale);
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Value.Contains(q)) return hits[i].Key;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                float travel = trackRect.Height - thumbRect.Height;
                if (travel > 0) scroll = Math.Max(0, Math.Min(scrollMax, dragStartScroll + (e.Y / scale - dragStartY) * scrollMax / travel));
                Invalidate();
                return;
            }
            string h = HitTest(e.Location);
            if (h == hover) return;
            hover = h;
            Cursor = h != null && !h.StartsWith("row:") && !h.StartsWith("plrow:") && h != "scrolltrack" ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null) { hover = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            float y = e.Y / scale, x = e.X / scale;
            string down = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && down == "scrollthumb")
            {
                dragging = true; suppressClick = true; dragStartY = y; dragStartScroll = scroll; Capture = true;
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left && down == "scrolltrack")
            {
                suppressClick = true;
                float page = trackRect.Height - 40;
                scroll = Math.Max(0, Math.Min(scrollMax, scroll + (y < thumbRect.Y ? -page : page)));
                Invalidate();
                return;
            }
            bool dragZone = y < 76 || (x < SideW && y < 80);
            if (e.Button == MouseButtons.Left && dragZone && HitTest(e.Location) == null)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (page != Page.Channels) return;
            scroll = Math.Max(0, Math.Min(scrollMax, scroll - e.Delta / 120f * 60f));
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Right)
            {
                string pid = PlaylistIdAt(HitTest(e.Location));
                if (pid != null) ShowPlaylistMenu(pid, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (suppressClick) { suppressClick = false; return; }   // this click ended a scrollbar drag
            string id = HitTest(e.Location);
            if (id == null) { ActiveControl = null; Invalidate(); return; }
            string arg = id.IndexOf(':') >= 0 ? id.Substring(id.IndexOf(':') + 1) : null;
            if (id == "close") CloseOrHide();
            else if (id == "min") WindowState = FormWindowState.Minimized;
            else if (id.StartsWith("nav:")) CurrentPage = (Page)Enum.Parse(typeof(Page), arg);
            else if (id == "goto:channels") CurrentPage = Page.Channels;
            else if (id == "retry") { if (obs.State == ObsState.AuthFailed) CurrentPage = Page.Settings; else obs.Reconnect(); }
            else if (id == "pause") { engine.SetPaused(!settings.Paused); settings.Paused = !settings.Paused; UpdateAnim(); ShowToast(settings.Paused ? "Protection paused" : "Protection on"); }
            else if (id.StartsWith("toggle:")) ToggleChannel(arg);
            else if (id.StartsWith("app:")) ShowAppMenu(arg, e.Location);
            else if (id.StartsWith("action:")) ShowActionMenu(arg, e.Location);
            else if (id.StartsWith("safe:")) ShowSafeMenu(arg, e.Location);
            else if (id.StartsWith("link:")) { CurrentPage = Page.Channels; ShowToast("Switch on the OBS source that carries " + AppKey.Pretty(arg) + ", and set Listens to."); }
            else if (id == "refresh") { engine.Refresh(); ShowToast("Reloading sources from OBS…"); }
            else if (id == "showhidden") { showHidden = !showHidden; scroll = 0; }
            else if (id.StartsWith("filter:")) { filter = arg; scroll = 0; }
            else if (id == "clearfilters") { filter = "all"; tbSearch.Text = ""; scroll = 0; }
            else if (id == "sort")
            {
                var m = NewMenu();
                Add(m, "Suggested  (in use and music sources first)", !sortByName, () => { sortByName = false; Invalidate(); });
                Add(m, "Name A–Z", sortByName, () => { sortByName = true; Invalidate(); });
                m.Show(this, e.Location);
            }
            else if (id.StartsWith("hide:")) { SetHidden(arg, true); ShowToast(arg + " hidden. \"Show hidden\" at the top brings it back."); }
            else if (id.StartsWith("unhide:")) { SetHidden(arg, false); ShowToast(arg + " is visible again"); }
            else if (id == "connect") Connect();
            else if (id == "obscfg") UseObsConfig();
            else if (id == "allowadd") AddAllow();
            else if (id.StartsWith("allowdel:")) { int k = int.Parse(arg); var l = settings.AllowList(); if (k < l.Count) engine.RemoveAllow(l[k]); }
            else if (id == "opt:tray") { settings.CloseToTray = !settings.CloseToTray; settings.Save(); UpdateAnim(); }
            else if (id == "opt:hidden") { settings.StartHidden = !settings.StartHidden; settings.Save(); UpdateAnim(); }
            else if (id == "restore:quiet") { settings.Restore = RestoreMode.AfterQuiet; settings.Save(); engine.Reevaluate(); }
            else if (id == "restore:end") { settings.Restore = RestoreMode.TrackEnd; settings.Save(); engine.Reevaluate(); }
            else if (id == "opt:listen") { settings.ListenToAudio = !settings.ListenToAudio; settings.Save(); UpdateAnim(); ShowToast(settings.ListenToAudio ? "Listening to your sources' audio" : "Audio recognition off (Now Playing only)"); }
            else if (id.StartsWith("risk:")) ShowRiskMenu((RiskLevel)Enum.Parse(typeof(RiskLevel), arg), e.Location);
            else if (id == "pl:add") AddPlaylist(null);
            else if (id == "pl:file") ImportFile();
            else if (id.StartsWith("plmenu:")) ShowPlaylistMenu(arg, e.Location);
            else if (id.StartsWith("pldel:")) DeletePlaylist(arg);
            else if (id == "help:lists") ShowHelp("Stream-safe lists",
                "Songs from the labels and artists on a switched-on list count as stream-safe, so SongSentry leaves them alone.\n\n" +
                "FREE lists (StreamBeats, NCS, FiXT) are on by default: anyone may stream them.\n\n" +
                "PAID lists have no login. Epidemic Sound, Monstercat, Artlist and others don't let apps check your subscription, " +
                "so the switch simply means \"I have a license for this library\". Turn one on only if you really do: " +
                "their music is only safe for subscribers, and you may also need to add your channel in their own website.\n\n" +
                "Your own lists and playlists are on the Playlists page.");
            else if (id == "help:playlists") ShowHelp("Safe playlists",
                "Every song in a playlist you add here counts as stream-safe. Rename or delete whole playlists from their ••• menu; the switch turns one off without deleting it.\n\n" +
                "Spotify or YouTube link\nPaste a playlist's Share link. YouTube and YouTube Music links read the whole playlist (public or unlisted). " +
                "Spotify links only share the first 100 songs, and only of public playlists.\n\n" +
                "Songs copied from Spotify (big or private playlists, Liked Songs)\nOpen the playlist in the Spotify app, click a song, press Ctrl+A then Ctrl+C, " +
                "then paste into Add playlist. Every song is read, but Spotify limits how fast songs can be looked up, so this can be " +
                "really slow (several minutes for a big playlist). Keep SongSentry open until it says the playlist is saved; " +
                "songs it has looked up once are remembered, so pasting the playlist again later is quick.\n\n" +
                "Pear Desktop (YouTube Music)\nPlay the playlist in Pear and click Read Pear's playlist. Turn on Pear's API Server plugin first (Plugins menu); " +
                "the first time, Pear asks you to Allow SongSentry.\n\n" +
                "Scan (any player)\nWindows only tells SongSentry which song is playing, so Scan presses \"next\" every couple of seconds and saves each song until the playlist starts over.\n\n" +
                "Playlists added from a link are checked again each time SongSentry starts, so songs you add later count too. " +
                "Only add playlists whose songs you know are safe to play on stream.");
            else if (id.StartsWith("list:")) { if (settings.Lists.Enabled.Contains(arg)) settings.Lists.Enabled.Remove(arg); else settings.Lists.Enabled.Add(arg); settings.Save(); engine.Reevaluate(); }
            else if (id.StartsWith("listdel:")) { settings.Lists.Remove(arg); settings.Save(); engine.Reevaluate(); ShowToast("List removed"); }
            else if (id.StartsWith("safeapp:")) { bool on = settings.IsSafeApp(arg); if (on) engine.RemoveAllow(settings.AllowList().First(x => x.Equals("app:" + arg, StringComparison.OrdinalIgnoreCase))); else engine.AddAllow("app:" + arg); ShowToast(on ? AppKey.Pretty(arg) + " is checked normally again" : "All music from " + AppKey.Pretty(arg) + " counts as safe"); }
            else if (id == "scan:app") ShowScanAppMenu(e.Location);
            else if (id == "scan:go") StartOrStopScan(null);
            else if (id == "pear:go") ReadPear();
            else if (id == "opt:acoustid") { settings.UseAcoustId = !settings.UseAcoustId; settings.Save(); UpdateAnim(); ShowToast(settings.UseAcoustId ? "AcoustID on (free)" : "AcoustID off"); }
            else if (id == "mem:clear")
            {
                if (MessageBox.Show(this, "Forget all " + recognizer.Library.Count + " learned songs? SongSentry will re-learn them as they play.", "SongSentry",
                                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) { recognizer.Library.Clear(); ShowToast("Song memory cleared"); }
            }
            else if (id == "url:audiotag") OpenUrl("https://user.audiotag.info/");
            else if (id == "url:audd") OpenUrl("https://dashboard.audd.io/");
            else if (id == "opt:skip")
            {
                settings.AutoSkip = !settings.AutoSkip; settings.Save(); UpdateAnim();
                ShowToast(settings.AutoSkip ? "Licensed songs from media players get skipped" : "Auto-skip is off");
            }
            else if (id == "opt:startwin")
            {
                if (Program.SetStartWithWindows(!startWithWindows)) startWithWindows = !startWithWindows;
                else ShowToast("Couldn't change the Windows start-up setting");
                UpdateAnim();
            }
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5) engine.Refresh();
            else if (e.Control && e.KeyCode == Keys.F) { CurrentPage = Page.Channels; LayoutBoxes(); tbSearch.Focus(); }
            else if (e.Control && e.KeyCode == Keys.D1) CurrentPage = Page.Live;
            else if (e.Control && e.KeyCode == Keys.D2) CurrentPage = Page.Channels;
            else if (e.Control && e.KeyCode == Keys.D3) CurrentPage = Page.Recognition;
            else if (e.Control && e.KeyCode == Keys.D4) CurrentPage = Page.SafeMusic;
            else if (e.Control && e.KeyCode == Keys.D5) CurrentPage = Page.Playlists;
            else if (e.Control && e.KeyCode == Keys.D6) CurrentPage = Page.Settings;
        }

        // ================================================================== actions

        bool startWithWindows = Program.StartsWithWindows();

        static string FormatSeconds(double s)
        {
            return s.ToString(s == Math.Floor(s) ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture);
        }

        void CommitDelay()
        {
            double v;
            string txt = tbDelay.Text.Trim().Replace(',', '.');
            if (double.TryParse(txt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                v = Math.Max(0, Math.Min(120, Math.Round(v, 1)));
                if (v != settings.RestoreDelay) { settings.RestoreDelay = v; settings.Save(); ShowToast("Sources come back after " + FormatSeconds(v) + " s of quiet"); }
            }
            tbDelay.Text = FormatSeconds(settings.RestoreDelay);
            Invalidate();
        }

        void SetHidden(string input, bool hide)
        {
            engine.SetHidden(input, hide);
        }

        void ToggleChannel(string input)
        {
            var c = settings.Find(input);
            var inf = engine.Inputs.FirstOrDefault(i => i.Name == input);
            var apps = c != null ? c.Apps : engine.NewChannel(input, inf).Apps;
            if (apps.Count == 0)
            {
                ShowToast("First choose which apps this source carries (Listens to).");
                return;
            }
            bool on = c == null || !c.Enabled;
            engine.SetChannel(input, ch => ch.Enabled = on);   // the engine owns channel changes; the switch follows on its next update
            ShowToast(on ? input + " is protected" : input + " is no longer protected");
        }

        ContextMenuStrip NewMenu()
        {
            return new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true, Font = new Font("Segoe UI", 13f * scale, GraphicsUnit.Pixel) };
        }

        void ShowAppMenu(string input, Point at)
        {
            var c = settings.Find(input);
            var inf = engine.Inputs.FirstOrDefault(i => i.Name == input);
            var current = new List<string>(c != null ? c.Apps : engine.NewChannel(input, inf).Apps);
            var carried = Engine.Carried(inf);
            var m = NewMenu();
            var head = new ToolStripMenuItem("Tick every app whose sound goes through this source") { Enabled = false };
            m.Items.Add(head);
            var apps = new List<string>();
            foreach (var a in current) if (!apps.Contains(a)) apps.Add(a);
            foreach (var a in carried) if (!apps.Contains(a) && Engine.KnownMediaApps.Contains(a)) apps.Add(a);
            foreach (var md in engine.Media) if (!apps.Contains(md.App)) apps.Add(md.App);
            foreach (var a in new[] { "spotify", "chrome", "msedge", "brave", "firefox", "opera", "tidal", "applemusic", "deezer", "vlc" })
                if (!apps.Contains(a)) apps.Add(a);
            foreach (string a in apps)
            {
                string label = AppKey.Pretty(a);
                if (inf != null && inf.Kind == "wasapi_process_output_capture" && inf.App == a) label += "   (this source captures it)";
                else if (carried.Contains(a)) label += "   (plays through this device)";
                else if (engine.Media.Any(md => md.App == a && md.State == PlayState.Playing)) label += "   (playing now)";
                string key = a;
                var item = new ToolStripMenuItem(label) { Checked = current.Contains(a) };
                item.Click += (s, e) => { item.Checked = !item.Checked; ToggleApp(input, key, item.Checked); };
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            DetectMode mode = c != null ? c.Mode : DetectMode.AnyMedia;
            var any = new ToolStripMenuItem("React to anything music apps play") { Checked = mode == DetectMode.AnyMedia };
            var known = new ToolStripMenuItem("Only songs confirmed by MusicBrainz") { Checked = mode == DetectMode.KnownSongs };
            any.Click += (s, e) => { any.Checked = true; known.Checked = false; engine.SetChannel(input, ch => ch.Mode = DetectMode.AnyMedia); };
            known.Click += (s, e) => { any.Checked = false; known.Checked = true; engine.SetChannel(input, ch => ch.Mode = DetectMode.KnownSongs); };
            m.Items.Add(any); m.Items.Add(known);
            var note = new ToolStripMenuItem("(Browsers are always checked on MusicBrainz first.)") { Enabled = false };
            m.Items.Add(note);
            // Stay open while ticking apps; close on click outside or Esc.
            m.Closing += (s, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
            m.Closed += (s, e) => Invalidate();
            m.Show(this, at);
        }

        void ToggleApp(string input, string app, bool on)
        {
            // On a new source the engine first creates the channel from the suggestion, then applies this tick.
            engine.SetChannel(input, ch => { ch.Apps.Remove(app); if (on) ch.Apps.Add(app); });
        }
        void ShowActionMenu(string input, Point at)
        {
            var c = settings.Find(input);
            ActionKind cur = c != null ? c.Action : ActionKind.StreamOnly;
            int duck = c != null ? c.DuckPercent : 20;
            var m = NewMenu();
            Add(m, "Mute on stream only  (recording keeps it)", cur == ActionKind.StreamOnly, () => engine.SetChannel(input, ch => ch.Action = ActionKind.StreamOnly));
            Add(m, "Mute everywhere", cur == ActionKind.Mute, () => engine.SetChannel(input, ch => ch.Action = ActionKind.Mute));
            var dk = new ToolStripMenuItem("Turn down") { Checked = cur == ActionKind.Duck };
            foreach (int p in new[] { 5, 10, 20, 30, 50 })
            {
                int pct = p;
                var it = new ToolStripMenuItem("to " + p + "%") { Checked = cur == ActionKind.Duck && duck == p };
                it.Click += (s, e) => engine.SetChannel(input, ch => { ch.Action = ActionKind.Duck; ch.DuckPercent = pct; });
                dk.DropDownItems.Add(it);
            }
            ((ToolStripDropDownMenu)dk.DropDown).Renderer = new DarkMenuRenderer();
            m.Items.Add(dk);
            Add(m, "Warn only  (never changes OBS)", cur == ActionKind.Warn, () => engine.SetChannel(input, ch => ch.Action = ActionKind.Warn));
            m.Show(this, at);
        }

        void ShowSafeMenu(string input, Point at)
        {
            var v = engine.Channels.FirstOrDefault(x => x.Input == input);
            if (v == null || v.Media == null) return;
            var m = NewMenu();
            Add(m, "This song is safe:  " + Engine.SongText(v.Media), false, () => { engine.AllowCurrent(input, false); ShowToast("Added to your allow list"); });
            if (!string.IsNullOrEmpty(v.Media.Artist))
                Add(m, "Everything by " + v.Media.Artist + " is safe", false, () => { engine.AllowCurrent(input, true); ShowToast("Added to your allow list"); });
            m.Show(this, at);
        }

        static void Add(ContextMenuStrip m, string text, bool check, Action a)
        {
            var it = new ToolStripMenuItem(text) { Checked = check };
            it.Click += (s, e) => a();
            m.Items.Add(it);
        }

        void Connect()
        {
            int port;
            if (!int.TryParse(tbPort.Text.Trim(), out port) || port < 1 || port > 65535) { ShowToast("The port must be a number, usually 4455"); return; }
            settings.Host = string.IsNullOrWhiteSpace(tbHost.Text) ? "127.0.0.1" : tbHost.Text.Trim();
            settings.Port = port;
            settings.Password = tbPassword.Text;
            settings.Save();
            obs.Configure(settings.Host, settings.Port, settings.Password);
            ShowToast("Connecting to OBS…");
        }

        void UseObsConfig()
        {
            int port; string pw; bool enabled;
            if (!Settings.TryReadObsConfig(out port, out pw, out enabled)) { ShowToast("Couldn't find OBS's WebSocket settings on this PC"); return; }
            tbHost.Text = "127.0.0.1"; tbPort.Text = port.ToString(); tbPassword.Text = pw;
            Connect();
            if (!enabled) ShowToast("Loaded. OBS's saved settings say the server is off: enable it in OBS.");
            else ShowToast("Loaded OBS's saved settings. If it fails, click OK in OBS's WebSocket window once so it saves them.");
        }

        void AddAllow()
        {
            string t = tbAllow.Text.Trim();
            if (t.Length == 0) return;
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            string e = t.StartsWith("label:", StringComparison.OrdinalIgnoreCase) ? "label:" + t.Substring(6).Trim()
                     : dash > 0 ? "track:" + t.Substring(0, dash).Trim() + " - " + t.Substring(dash + 3).Trim() : "artist:" + t;
            engine.AddAllow(e);
            tbAllow.Text = "";
            ShowToast("Allowed: " + t);
        }

        // ================================================================== window / tray

        void CloseOrHide()
        {
            if (settings.CloseToTray && !quitting)
            {
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "SongSentry is still guarding", "It keeps running in the tray. Right-click the icon to quit.", ToolTipIcon.Info);
                }
            }
            else Quit();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            QueueRepaint();
        }

        public void Quit()
        {
            quitting = true;
            engine.RestoreAll(3000);
            tray.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && settings.CloseToTray && !quitting) { e.Cancel = true; Hide(); return; }
            if (!quitting) { quitting = true; engine.RestoreAll(2500); }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        ToolStripMenuItem trayPause;

        void BuildTrayMenu()
        {
            trayMenu.Items.Clear();
            trayMenu.Font = new Font("Segoe UI", 12.5f * scale, GraphicsUnit.Pixel);
            trayMenu.Items.Add("Open SongSentry", null, (s, e) => ShowFromTray());
            trayPause = new ToolStripMenuItem(settings.Paused ? "Resume protection" : "Pause protection");
            trayPause.Click += (s, e) => { engine.SetPaused(!settings.Paused); settings.Paused = !settings.Paused; QueueRepaint(); };
            trayMenu.Items.Add(trayPause);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Quit (restores all sources)", null, (s, e) => Quit());
        }
        void UpdateTray()
        {
            string state = obs.State != ObsState.Connected ? "OBS not connected" : settings.Paused ? "paused" : Protecting ? "protecting now" : "guarding";
            string t = "SongSentry: " + state;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void OnNotify(string title, string text, bool warnOnly)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                if (ContainsFocus) return;   // the Live page already shows it
                tray.ShowBalloonTip(3500, title, string.IsNullOrEmpty(text) ? " " : text, warnOnly ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (settings.ChannelList().Length == 0 && obs.State == ObsState.Connected) page = Page.Channels;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { LayoutBoxes(); UpdateAnim(); } else anim.Stop();
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            if (page != Page.Live) BeginInvoke((Action)LayoutBoxes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Dispose(); secondTick.Dispose(); tray.Dispose(); trayMenu.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
