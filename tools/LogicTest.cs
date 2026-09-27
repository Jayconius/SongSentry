// Logic tests for the SongSentry engine against a fake OBS. Run by build.ps1; exits non-zero on failure.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SongSentry
{
    static class LogicTest
    {
        static int failures, passes;
        static DateTime now = new DateTime(2026, 1, 1);

        static void Check(bool ok, string what)
        {
            if (ok) passes++; else { failures++; Console.WriteLine("  FAIL: " + what); }
        }

        static MediaInfo Song(string app, string artist, string title, PlayState st)
        {
            return new MediaInfo { Aumid = app + ".exe", App = app, Artist = artist, Title = title, State = st };
        }

        static Engine Make(FakeObs obs, Settings s, bool keepJournal = false)
        {
            if (!keepJournal && File.Exists(Paths.File("restore.json"))) File.Delete(Paths.File("restore.json"));
            var e = new Engine(s, obs, () => now) { Synchronous = true };
            e.OnObsState(true);
            return e;
        }

        static void Advance(Engine e, double seconds) { now = now.AddSeconds(seconds); e.TickForTest(); }

        static int Main()
        {
            Paths.Data = Path.Combine(Path.GetTempPath(), "SongSentryLogicTest");
            if (Directory.Exists(Paths.Data)) Directory.Delete(Paths.Data, true);

            Test("AppKey normalisation", () =>
            {
                Check(AppKey.Of("Spotify.exe") == "spotify", "Spotify.exe");
                Check(AppKey.Of("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "spotify", "store AUMID");
                Check(AppKey.Of(@"C:\Program Files\Google\Chrome\Application\chrome.exe") == "chrome", "path");
                Check(AppKey.Of("MSEdge") == "msedge", "MSEdge");
                Check(AppKey.IsBrowser("brave") && !AppKey.IsBrowser("spotify"), "IsBrowser");
            });

            Test("YouTube title parsing", () =>
            {
                string a, t;
                MusicBrainz.Parse("Taylor Swift - I Knew You Were Trouble (Official Video)", "TaylorSwiftVEVO", out a, out t);
                Check(a == "Taylor Swift" && t == "I Knew You Were Trouble", "artist - title (official video): " + a + " | " + t);
                MusicBrainz.Parse("Blinding Lights", "The Weeknd - Topic", out a, out t);
                Check(a == "The Weeknd" && t == "Blinding Lights", "topic channel: " + a + " | " + t);
                MusicBrainz.Parse("Sylver - Lay All Your Love On Me (Mark With a K Remix)", "", out a, out t);
                Check(t.Contains("Remix"), "keeps remix: " + t);
                MusicBrainz.Parse("Daft Punk – Get Lucky ft. Pharrell Williams [Official Audio]", "", out a, out t);
                Check(a == "Daft Punk" && t == "Get Lucky", "en dash + feat: " + a + " | " + t);
            });

            Test("Allow list matching", () =>
            {
                var s = new Settings();
                s.Allow.Add("artist:streambeats");
                s.Allow.Add("track:sylver - lay all your love on me");
                Check(s.IsAllowed("StreamBeats", "Anything"), "artist");
                Check(s.IsAllowed("Harris Heller, StreamBeats", "x"), "artist in a credit list");
                Check(s.IsAllowed("Sylver", "Lay All Your Love On Me"), "track");
                Check(!s.IsAllowed("Taylor Swift", "Lay All Your Love On Me"), "different artist");
                Check(!s.IsAllowed("StreamBeatsX", "x"), "no partial artist match");
            });

            Test("Stream-only mute removes stream + Twitch VOD tracks, restores after the delay", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "wasapi_process_output_capture", false, 1, true, true, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.StreamOnly });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Taylor Swift", "I Knew You Were Trouble.", PlayState.Playing) });
                Check(obs.Calls.SequenceEqual(new[] { "tracks Music 1=off,2=off" }), "applied: " + string.Join("; ", obs.Calls));
                Check(obs.In["Music"].Tracks["3"], "recording track 3 untouched");
                obs.Calls.Clear();
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Taylor Swift", "I Knew You Were Trouble.", PlayState.Paused) });
                Check(obs.Calls.Count == 0, "not restored immediately");
                Advance(e, 1.0);
                Check(obs.Calls.Count == 0, "not restored after 1 s");
                Advance(e, 1.5);
                Check(obs.Calls.SequenceEqual(new[] { "tracks Music 1=on,2=on" }), "restored: " + string.Join("; ", obs.Calls));
            });

            Test("Resume within the delay keeps it muted (no flapping)", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true, false);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                var play = new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) };
                e.OnMedia(play);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Paused) });
                Advance(e, 1);
                e.OnMedia(play);
                Advance(e, 5);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True" }), "one mute, no restore: " + string.Join("; ", obs.Calls));
            });

            Test("Already-muted source: nothing changed, never unmuted by us", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", true, 1, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) });
                e.OnMedia(new List<MediaInfo>());
                Advance(e, 5);
                Check(obs.Calls.Count == 0 && obs.In["Music"].Muted, "no calls: " + string.Join("; ", obs.Calls));
            });

            Test("Duck restores the exact original volume", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 0.8, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Duck, DuckPercent = 20 });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) });
                e.OnMedia(new List<MediaInfo>());
                Advance(e, 3);
                Check(obs.Calls.SequenceEqual(new[] { "volume Music 0.16", "volume Music 0.8" }), string.Join("; ", obs.Calls));
            });

            Test("Allowed songs are left alone", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings(); s.Allow.Add("artist:streambeats");
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "StreamBeats", "Lofi 1", PlayState.Playing) });
                Check(obs.Calls.Count == 0, "no action");
                Check(e.Channels[0].Status == ChannelStatus.Allowed, "status Allowed");
            });

            Test("Manual change during a song is respected until the next song", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "Song 1", PlayState.Playing) });
                // the streamer unmutes by hand in OBS
                obs.In["Music"].Muted = false;
                e.OnObsEvent("InputMuteStateChanged", Json.Make("inputName", "Music", "inputMuted", false));
                Advance(e, 5);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True" }), "not re-muted: " + string.Join("; ", obs.Calls));
                Check(e.Channels[0].Status == ChannelStatus.Overridden, "status Overridden");
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "Song 2", PlayState.Playing) });
                Check(obs.Calls.Count == 2 && obs.Calls[1] == "mute Music True", "next song muted again: " + string.Join("; ", obs.Calls));
            });

            Test("Our own OBS echo events are not mistaken for manual changes", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                Engine e = null;
                obs.Echo = (t, d) => e.OnObsEvent(t, d);
                e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) });
                Check(e.Channels[0].Status == ChannelStatus.Protecting, "still protecting after echo");
                e.OnMedia(new List<MediaInfo>());
                Advance(e, 3);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True", "mute Music False" }), string.Join("; ", obs.Calls));
            });

            Test("Turning a channel off restores it immediately", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) });
                e.SetChannel("Music", c => c.Enabled = false);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True", "mute Music False" }), string.Join("; ", obs.Calls));
            });

            Test("Other apps and disabled channels are ignored", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true); obs.Add("Game", "x", false, 1, true);
                var s = new Settings();
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                s.Channels.Add(new Channel { Input = "Game", Enabled = false, App = "chrome", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("chrome", "A", "B", PlayState.Playing) });
                Check(obs.Calls.Count == 0, "nothing: " + string.Join("; ", obs.Calls));
            });

            Test("Browser: only confirmed songs are muted", () =>
            {
                var obs = new FakeObs(); obs.Add("Browser", "x", false, 1, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Browser", Enabled = true, App = "brave", Action = ActionKind.Mute, Mode = DetectMode.KnownSongs });
                var e = Make(obs, s);
                e.SongLookup = (a, t) => new SongMatch { Found = t == "I Knew You Were Trouble", Artist = a, Title = t, Score = 100 };
                e.OnMedia(new List<MediaInfo> { Song("brave", "SomeStreamer", "SEPTEMBER SUBATHON '26 | 1SUB = 2 minutes", PlayState.Playing) });
                Check(obs.Calls.Count == 0, "stream title not muted");
                e.OnMedia(new List<MediaInfo> { Song("brave", "TaylorSwiftVEVO", "Taylor Swift - I Knew You Were Trouble (Official Video)", PlayState.Playing) });
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True".Replace("Music", "Browser") }), "song muted: " + string.Join("; ", obs.Calls));
            });

            Test("Several apps share one source: any risky one protects it", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings();
                var ch = new Channel { Input = "Music", Enabled = true, Action = ActionKind.Mute };
                ch.Apps.Add("spotify"); ch.Apps.Add("brave");
                s.Channels.Add(ch);
                var e = Make(obs, s);
                e.SongLookup = (a, t) => new SongMatch { Found = t == "Blinding Lights", Artist = a, Title = t, Score = 100 };
                // Brave shows a stream (not a song) and Spotify is paused: nothing happens
                e.OnMedia(new List<MediaInfo> { Song("brave", "SomeStreamer", "SUBATHON day 3", PlayState.Playing), Song("spotify", "A", "B", PlayState.Paused) });
                Check(obs.Calls.Count == 0, "no action yet: " + string.Join("; ", obs.Calls));
                // now Spotify plays too: the source gets muted
                e.OnMedia(new List<MediaInfo> { Song("brave", "SomeStreamer", "SUBATHON day 3", PlayState.Playing), Song("spotify", "A", "B", PlayState.Playing) });
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True" }), "muted for Spotify: " + string.Join("; ", obs.Calls));
                Check(e.Channels[0].Media != null && e.Channels[0].Media.App == "spotify", "reports the Spotify song");
                // Spotify stops, Brave switches to a real song: still protected, no unmute in between
                e.OnMedia(new List<MediaInfo> { Song("brave", "The Weeknd", "The Weeknd - Blinding Lights (Official Video)", PlayState.Playing) });
                Advance(e, 5);
                Check(obs.Calls.Count == 1, "stays muted across apps: " + string.Join("; ", obs.Calls));
            });

            Test("Until the track is over: a paused risky track stays muted, a new track releases it", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings { Restore = RestoreMode.TrackEnd };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                Func<PlayState, double, MediaInfo> ts = (st, pos) => new MediaInfo { Aumid = "Spotify.exe", App = "spotify", Artist = "Taylor Swift", Title = "Trouble",
                    State = st, Duration = TimeSpan.FromSeconds(219), Position = TimeSpan.FromSeconds(pos), PositionAt = now };
                e.OnMedia(new List<MediaInfo> { ts(PlayState.Playing, 10) });
                e.OnMedia(new List<MediaInfo> { ts(PlayState.Paused, 60) });
                Advance(e, 30);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True" }), "held while paused: " + string.Join("; ", obs.Calls));
                Check(e.Channels[0].Status == ChannelStatus.Protecting, "status still Protecting");
                // the user moves on to an allowed track: released after the quiet delay
                s.Allow.Add("artist:StreamBeats");
                e.OnMedia(new List<MediaInfo> { new MediaInfo { Aumid = "Spotify.exe", App = "spotify", Artist = "StreamBeats", Title = "Lofi", State = PlayState.Playing } });
                Advance(e, 3);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True", "mute Music False" }), "released: " + string.Join("; ", obs.Calls));
            });

            Test("Until the track is over: paused at the very end counts as over", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings { Restore = RestoreMode.TrackEnd };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                var m = new MediaInfo { Aumid = "Spotify.exe", App = "spotify", Artist = "A", Title = "B", State = PlayState.Playing, Duration = TimeSpan.FromSeconds(200) };
                e.OnMedia(new List<MediaInfo> { m });
                var end = m.Clone(); end.State = PlayState.Paused; end.Position = TimeSpan.FromSeconds(199.5); end.PositionAt = now;
                e.OnMedia(new List<MediaInfo> { end });
                Advance(e, 3);
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True", "mute Music False" }), string.Join("; ", obs.Calls));
            });

            Test("Auto-skip sends next track, and stops after 5 in a row", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings { AutoSkip = true };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.StreamOnly });
                var e = Make(obs, s);
                int skipsSent = 0;
                e.Skipper = aumid => { skipsSent++; return true; };
                for (int k = 1; k <= 7; k++)
                {
                    e.OnMedia(new List<MediaInfo> { Song("spotify", "Artist", "Licensed song " + k, PlayState.Playing) });
                    Advance(e, 3);
                }
                Check(skipsSent == 5, "5 skips then stop: " + skipsSent);
                Check(obs.Calls.Count == 1, "source stayed stripped the whole time: " + string.Join("; ", obs.Calls));
                // a safe song re-arms it
                s.Allow.Add("artist:StreamBeats");
                e.OnMedia(new List<MediaInfo> { Song("spotify", "StreamBeats", "Lofi", PlayState.Playing) });
                Advance(e, 70);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Artist", "Licensed song 8", PlayState.Playing) });
                Check(skipsSent == 6, "skipping again after a safe song: " + skipsSent);
            });

            Test("Warn-only never skips", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings { AutoSkip = true };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Warn });
                var e = Make(obs, s);
                int skipsSent = 0;
                e.Skipper = aumid => { skipsSent++; return true; };
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) });
                Check(skipsSent == 0 && obs.Calls.Count == 0, "no skip, no OBS change");
            });

            Test("Songs heard in the audio are protected, then restored when they stop", () =>
            {
                var obs = new FakeObs(); obs.Add("Game", "x", false, 1, true, true);
                var s = new Settings();
                s.Channels.Add(new Channel { Input = "Game", Enabled = true, Action = ActionKind.StreamOnly });   // no app: audio only
                var e = Make(obs, s);
                var song = new SongInfo { Artist = "SWV", Title = "Rain", Label = "RCA Records", Source = "AudD" };
                e.OnAudio("Game", new SongHit { Song = song, Votes = 40 });
                Check(obs.Calls.SequenceEqual(new[] { "tracks Game 1=off,2=off" }), "stripped: " + string.Join("; ", obs.Calls));
                Check(e.Channels[0].Media != null && e.Channels[0].Media.Title == "Rain", "reports the heard song");
                // a missed window or two doesn't flap
                Advance(e, 2); e.OnAudio("Game", null);
                Advance(e, 2); e.OnAudio("Game", new SongHit { Song = song, Votes = 30 });
                Check(obs.Calls.Count == 1, "no flapping: " + string.Join("; ", obs.Calls));
                // song over: hold 5 s + restore delay 2 s
                for (int k = 0; k < 5; k++) { Advance(e, 2); e.OnAudio("Game", null); }
                Check(obs.Calls.SequenceEqual(new[] { "tracks Game 1=off,2=off", "tracks Game 1=on,2=on" }), "restored: " + string.Join("; ", obs.Calls));
            });

            Test("Heard songs: label allow list and safe songs are left alone", () =>
            {
                var obs = new FakeObs(); obs.Add("Game", "x", false, 1, true);
                var s = new Settings(); s.Allow.Add("label:Audio Captain");
                s.Channels.Add(new Channel { Input = "Game", Enabled = true, Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnAudio("Game", new SongHit { Song = new SongInfo { Artist = "The Sound Project", Title = "Soundtrack", Label = "Audio Captain", Source = "AudD" }, Votes = 50 });
                e.OnAudio("Game", new SongHit { Song = new SongInfo { Artist = "Me", Title = "My Song", Source = "Now Playing", Safe = true }, Votes = 50 });
                Check(obs.Calls.Count == 0, "no action: " + string.Join("; ", obs.Calls));
                Check(e.Channels[0].Status == ChannelStatus.Allowed, "status Allowed");
                Check(s.IsAllowed("x", "y", "Audio Captain Ltd") && !s.IsAllowed("x", "y", "Audio Captains"), "label prefix matching");
            });

            Test("Old settings files with a single app still load", () =>
            {
                File.WriteAllText(Paths.File("settings.json"), "{\"channels\":[{\"input\":\"Music\",\"enabled\":true,\"app\":\"spotify\",\"action\":\"Mute\"}]}");
                var l = Settings.Load();
                Check(l.Channels.Count == 1 && l.Channels[0].Apps.Count == 1 && l.Channels[0].Apps[0] == "spotify", "migrated to Apps");
            });

            Test("Crash recovery: the journal restores what we held", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 0.5, true, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.StreamOnly });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "A", "B", PlayState.Playing) });
                Check(File.Exists(Paths.File("restore.json")), "journal written");
                obs.Calls.Clear();
                // "crash": a brand-new engine starts while the source is still stripped from the stream track
                var e2 = Make(obs, s, true);
                Check(obs.Calls.SequenceEqual(new[] { "tracks Music 1=on,2=on" }), "restored from journal: " + string.Join("; ", obs.Calls));
                Check(!File.Exists(Paths.File("restore.json")), "journal cleared");
            });

            Test("Log redaction hides tokens", () =>
            {
                string r = Log.Redact("url https://x.io/widget?client_secret=abc&refresh_token=def \"password\":\"hunter2\"");
                Check(!r.Contains("abc") && !r.Contains("def") && !r.Contains("hunter2"), r);
            });

            Test("Settings round-trip (password encrypted at rest)", () =>
            {
                var s = new Settings { Password = "pw123", Port = 4456 };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Duck, DuckPercent = 35 });
                s.Allow.Add("artist:streambeats");
                s.Save();
                string raw = File.ReadAllText(Paths.File("settings.json"));
                Check(!raw.Contains("pw123"), "password not in plain text");
                var l = Settings.Load();
                Check(l.Password == "pw123" && l.Port == 4456 && l.Channels.Count == 1 && l.Channels[0].DuckPercent == 35
                      && l.Channels[0].Action == ActionKind.Duck && l.Allow.Count == 1, "round trip");
            });

            Console.WriteLine(failures == 0 ? "  all " + passes + " checks passed" : "  " + failures + " FAILED, " + passes + " passed");
            return failures == 0 ? 0 : 1;
        }

        static void Test(string name, Action body)
        {
            int before = failures;
            try { body(); }
            catch (Exception e) { failures++; Console.WriteLine("  FAIL: exception " + e); }
            Console.WriteLine((failures == before ? "  ok   " : "  FAIL ") + name);
        }
    }
}
