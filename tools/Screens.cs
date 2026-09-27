// Renders README screenshots of the real MainForm with a fake OBS and made-up sources/songs (nothing from a real
// setup or stream), plus the README graphics. Never touches the real OBS, settings or song memory.
// Usage: Screens <outdir>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SongSentry
{
    static class Screens
    {
        [STAThread]
        static int Main(string[] a)
        {
            Paths.Data = Path.Combine(Path.GetTempPath(), "SongSentryScreens");
            if (Directory.Exists(Paths.Data)) Directory.Delete(Paths.Data, true);
            Directory.CreateDirectory(a[0]);

            var fake = new FakeObs();
            fake.Add("Music", "wasapi_process_output_capture", false, 0.62, true, false);
            fake.Add("Game Audio", "wasapi_process_output_capture", false, 0.6, true, true);
            fake.Add("Browser", "wasapi_process_output_capture", false, 1, true, true);
            fake.Add("Mic", "wasapi_input_capture", false, 1, true, true);
            fake.Add("Discord", "wasapi_process_output_capture", true, 0.56, true, true);
            fake.Add("Alerts", "browser_source", false, 1, true, true);
            fake.Add("Intro video", "ffmpeg_source", false, 1, true, true, true, true, true, true);
            fake.Add("BRB loop", "ffmpeg_source", false, 1, true, true, true, true, true, true);
            fake.Window = "Spotify:Chrome_WidgetWin_1:Spotify.exe";

            var settings = new Settings { Restore = RestoreMode.TrackEnd, AutoSkip = true, RiskIndependent = RiskAction.Warn };
            settings.Lists.FromJson(null);
            settings.Lists.Enabled.Add("epidemic");
            settings.Allow.Add("app:pretzel");
            settings.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.StreamOnly });
            settings.Channels.Add(new Channel { Input = "Game Audio", Enabled = true, Action = ActionKind.StreamOnly });
            settings.Channels.Add(new Channel { Input = "Browser", Enabled = true, App = "brave", Action = ActionKind.Duck, DuckPercent = 20, Mode = DetectMode.KnownSongs });
            settings.Hidden.Add("BRB loop");
            settings.Allow.Add("artist:StreamBeats");
            settings.Allow.Add("artist:Pretzel");
            settings.Allow.Add("label:Epidemic Sound");

            // a small made-up song memory (tones stand in for audio, only so the page shows learned lengths)
            var lib = new SongLibrary();
            var songs = new[]
            {
                new[] { "The Midnight Arcade", "Neon Skyline", "Night Drive Records", "Now Playing", "214" },
                new[] { "Coastal Echo", "Summer Static", "Sunwave Music", "AudD", "96" },
                new[] { "StreamBeats", "Lofi Afternoon", "StreamBeats", "Now Playing", "180" },
                new[] { "Velvet Parade", "Glass Hearts", "Heartline Records", "AudioTag", "142" },
            };
            foreach (var s in songs)
            {
                var info = lib.GetOrAdd(s[0], s[1], "", s[2], s[3], s[0] == "StreamBeats");
                lib.Learn(info, Tones(int.Parse(s[4]), s[1].GetHashCode()), 0);
            }

            var engine = new Engine(settings, fake, null) { Synchronous = true };
            engine.SongLookup = (ar, ti) => new SongMatch { Found = false };
            engine.RiskLookup = (ar, ti) => ti == "Neon Skyline" ? new RiskInfo { Level = RiskLevel.Major, Label = "Night Drive Records", Owner = "Sony Music" }
                                          : new RiskInfo { Level = RiskLevel.Independent, Label = "Sunwave Music" };
            engine.OnObsState(true);
            engine.OnMedia(new List<MediaInfo>
            {
                new MediaInfo { Aumid = "Spotify.exe", App = "spotify", Title = "Neon Skyline", Artist = "The Midnight Arcade", Album = "Night Drive",
                                State = PlayState.Playing, Duration = TimeSpan.FromSeconds(214), Position = TimeSpan.FromSeconds(83), PositionAt = DateTime.UtcNow },
                new MediaInfo { Aumid = "brave.exe", App = "brave", Title = "Lo-fi beats to stream to (24/7 radio)", Artist = "Chill Channel", State = PlayState.Playing },
            });
            engine.OnAudio("Game Audio", new SongHit { Song = lib.Songs().Find(x => x.Title == "Summer Static"), Votes = 48 });

            var obs = new ObsConnection();
            obs.SetPreviewState(ObsState.Connected, "5.7.4");
            var form = new MainForm(settings, engine, obs, new NowPlayingWatcher(), new Recognizer(settings, engine, lib));
            foreach (Page p in new[] { Page.Live, Page.Channels, Page.Recognition, Page.SafeMusic, Page.Settings })
            {
                form.CurrentPage = p;
                using (var bmp = new Bitmap((int)MainForm.W * 2, (int)MainForm.H * 2))
                {
                    using (var g = Graphics.FromImage(bmp)) form.RenderTo(g, 2f);   // 2x for crisp README images
                    bmp.Save(Path.Combine(a[0], "screen-" + p.ToString().ToLowerInvariant() + ".png"));
                }
            }
            form.Dispose();
            Graphics2.RenderAll(a[0]);
            return 0;
        }

        static short[] Tones(int seconds, int seed)
        {
            var rnd = new Random(seed);
            var o = new short[seconds * AudioCapture.Rate];
            double f1 = 220, f2 = 330, f3 = 440;
            for (int i = 0; i < o.Length; i++)
            {
                if (i % (AudioCapture.Rate / 2) == 0) { f1 = 150 + rnd.Next(600); f2 = f1 * 1.5; f3 = 400 + rnd.Next(1500); }
                double t = i / (double)AudioCapture.Rate;
                o[i] = (short)(6000 * (Math.Sin(2 * Math.PI * f1 * t) + Math.Sin(2 * Math.PI * f2 * t) + 0.6 * Math.Sin(2 * Math.PI * f3 * t)));
            }
            return o;
        }
    }
}
