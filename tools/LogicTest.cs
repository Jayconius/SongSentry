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
            e.RiskLookup = (a, t) => new RiskInfo { Level = RiskLevel.Major, Label = "Test Records", Reason = "test" };   // no network in tests
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

            Test("Music detector works on 8 kHz audio (regression: crashed at Nyquist)", () =>
            {
                var x = new short[5 * 8000];
                var rnd = new Random(2);
                for (int k = 0; k < x.Length; k++) x[k] = (short)(3000 * Math.Sin(2 * Math.PI * 440 * k / 8000.0) + rnd.Next(-500, 500));
                bool music = MusicDetector.LooksLikeMusic(x, 8000);
                Check(true, "no exception");
                var noise = new short[5 * 8000];
                for (int k = 0; k < noise.Length; k++) noise[k] = (short)rnd.Next(-8000, 8000);
                Check(!MusicDetector.LooksLikeMusic(noise, 8000), "white noise is not music");
                Check(!MusicDetector.LooksLikeMusic(new short[8000], 8000), "too short / silence is not music");
            });

            Test("Same song via Now Playing and via the audio counts as one song", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings(); s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Taylor Swift", "Trouble", PlayState.Playing) });
                e.OnAudio("Music", new SongHit { Song = new SongInfo { Artist = "Taylor Swift", Title = "Trouble", Source = "Now Playing" }, Votes = 40 });
                e.OnMedia(new List<MediaInfo>());   // player gone, audio still hears it
                Check(!e.Events.Any(x => x.Text.StartsWith("Next song")), "no 'next song' for the same song: " + string.Join(" | ", e.Events.Select(x => x.Text)));
                Check(obs.Calls.Count == 1, "muted once: " + string.Join("; ", obs.Calls));
            });

            Test("Titles from different services match (punctuation / case)", () =>
            {
                Check(TextNorm.Norm("I Knew You Were Trouble.") == TextNorm.Norm("i knew you were  trouble"), "trailing dot, case, spaces");
                Check(SongLibrary.KeyOf("Taylor Swift", "Trouble.") == SongLibrary.KeyOf("TAYLOR SWIFT", "Trouble"), "library key");
                var s = new Settings(); s.Allow.Add("track:Sylver - Lay All Your Love On Me");
                Check(s.IsAllowed("Sylver", "Lay All Your Love on Me!"), "allow list ignores punctuation");
                var lib = new SongLibrary();
                var a1 = lib.GetOrAdd("Taylor Swift", "I Knew You Were Trouble", null, null, "AudioTag", false);
                var a2 = lib.GetOrAdd("Taylor Swift", "I Knew You Were Trouble.", null, null, "AcoustID", false);
                Check(a1 == a2 && lib.Count == 1, "one song in the memory, not two");
            });

            Test("Label risk: majors recognised by name, indies not", () =>
            {
                Check(RiskRater.MajorOf("Republic Records") == "Universal Music Group", "Republic -> UMG");
                Check(RiskRater.MajorOf("Big Machine Records, LLC") == "Universal Music Group", "Big Machine -> UMG");
                Check(RiskRater.MajorOf("RCA Records Label") == "Sony Music", "RCA -> Sony");
                Check(RiskRater.MajorOf("Atlantic Records") == "Warner Music Group", "Atlantic -> Warner");
                Check(RiskRater.MajorOf("Epic Sound Studio") == null, "'Epic Sound' is not Epic Records");
                Check(RiskRater.MajorOf("Arcade Music") == null, "'Arcade' does not contain the word RCA");
                Check(RiskRater.MajorOf("Monstercat") == null && RiskRater.MajorOf("NCS") == null, "stream-safe labels are not majors");
                var lists = new SafeLists(); lists.FromJson(null);
                Check(RiskRater.FromLabel("NoCopyrightSounds", lists).Level == RiskLevel.Safe, "NCS is safe (free list on by default)");
                Check(RiskRater.FromLabel("Monstercat Uncaged", lists).Level == RiskLevel.Independent, "paid list off by default");
                lists.Enabled.Add("monstercat");
                Check(RiskRater.FromLabel("Monstercat Uncaged", lists).Level == RiskLevel.Safe, "paid list switched on");
            });

            Test("Stream-safe list import: text lines and Exportify-style CSV", () =>
            {
                var t = SafeLists.Parse("# my safe list\nlabel: Chillhop Music\nartist: Kupla\nSylver - Lay All Your Love On Me\nLakey Inspired", "mine");
                Check(t.Labels.Count == 1 && t.Artists.Count == 2 && t.Tracks.Count == 1, "text: " + t.Labels.Count + "/" + t.Artists.Count + "/" + t.Tracks.Count);
                var c = SafeLists.Parse("\"Track URI\",\"Track Name\",\"Artist Name(s)\",\"Album Name\"\n\"spotify:track:1\",\"Chill Day\",\"Lakey Inspired, Someone\",\"A\"\n\"spotify:track:2\",\"Say \"\"Hi\"\"\",\"Kupla\",\"B\"", "csv");
                Check(c.Tracks.Count == 2 && c.Tracks[0] == "Lakey Inspired - Chill Day" && c.Tracks[1] == "Kupla - Say \"Hi\"", "csv: " + string.Join(" | ", c.Tracks));
                var lists = new SafeLists(); lists.Imported.Add(c); lists.Enabled.Add(c.Id);
                Check(lists.MatchTrack("Lakey Inspired", "Chill Day!") != null, "imported track matches");
            });

            Test("Pear Desktop queue parsing", () =>
            {
                string json = "{\"items\":[{\"playlistPanelVideoRenderer\":{\"title\":{\"runs\":[{\"text\":\"Blinding Lights\"}]},\"shortBylineText\":{\"runs\":[{\"text\":\"The Weeknd\"}]}}}," +
                              "{\"playlistPanelVideoWrapperRenderer\":{\"primaryRenderer\":{\"playlistPanelVideoRenderer\":{\"title\":{\"runs\":[{\"text\":\"Chill Day\"}]},\"longBylineText\":{\"runs\":[{\"text\":\"Lakey Inspired\"},{\"text\":\" \u2022 \"},{\"text\":\"Album\"}]}}}}}]}";
                var q = PearDesktop.ParseQueue(json);
                Check(q.Count == 2 && q[0] == "The Weeknd - Blinding Lights" && q[1] == "Lakey Inspired - Chill Day", string.Join(" | ", q));
            });

            Test("Playlist scan: marks every song safe, stops when the playlist loops", () =>
            {
                var songs = new[] { "A", "B", "C", "D", "E" };
                int idx = 0; var allowed = new List<string>();
                var sc = new PlaylistScanner(
                    () => new List<MediaInfo> { new MediaInfo { Aumid = "Spotify.exe", App = "spotify", Artist = "Artist", Title = songs[idx % songs.Length], State = PlayState.Playing } },
                    aumid => { idx++; return true; },
                    e2 => allowed.Add(e2)) { StepMs = 0, WaitMs = 500 };
                sc.Run("spotify");
                Check(allowed.Count == 5 && allowed[0] == "track:Artist - A" && allowed[4] == "track:Artist - E", string.Join(" | ", allowed));
                Check(sc.Status.Contains("looped"), sc.Status);
            });

            Test("Named playlists: saved, merged by name or link, renamed, saved to settings and deleted as a whole", () =>
            {
                var lists = new SafeLists(); lists.FromJson(null);
                int added;
                var a = lists.SavePlaylist("Stream Bangers", "Spotify", "https://open.spotify.com/playlist/AAAAAAAAAAAAAAAAAAAAAA",
                                           new[] { "Artist One - Song A", "Artist Two - Song B" }, 150, out added);
                Check(added == 2 && lists.Enabled.Contains(a.Id) && a.Playlist && a.Total == 150, "saved, on, 2 of 150");
                Check(lists.MatchTrack("Artist One", "Song A (Radio Edit)") == "Stream Bangers", "matches loosely");
                var b = lists.SavePlaylist("stream bangers", "Pasted", null, new[] { "Artist One - Song A", "Artist Three - Song C" }, 0, out added);
                Check(b == a && added == 1 && a.Tracks.Count == 3, "same name (any case) adds to it, skipping duplicates: " + added);
                Check(lists.MatchTrack("Artist Three", "Song C") == "Stream Bangers", "index sees added songs");
                var c = lists.SavePlaylist("Other name", "Spotify", "https://open.spotify.com/playlist/AAAAAAAAAAAAAAAAAAAAAA", new[] { "X - Y" }, 0, out added);
                Check(c == a && a.Tracks.Count == 4, "same link adds to it");
                lists.Rename(a.Id, "Bangers");
                Check(lists.MatchTrack("Artist Two", "Song B") == "Bangers", "renamed");
                var back = new SafeLists(); back.FromJson(Json.Read(Json.Write(lists.ToJson())));
                var r = back.ById(a.Id);
                Check(r != null && r.Name == "Bangers" && r.Tracks.Count == 4 && r.Playlist && r.Source == "Spotify" && r.Total == 150
                      && r.Added.Date == DateTime.Now.Date && back.Enabled.Contains(a.Id), "saved to settings and back");
                lists.Enabled.Remove(a.Id);
                Check(lists.MatchTrack("Artist Two", "Song B") == null, "switched off: not safe");
                lists.Enabled.Add(a.Id); lists.Remove(a.Id);
                Check(lists.MatchTrack("Artist Two", "Song B") == null && lists.Mine.Count == 0, "deleted with all its songs");
                Check(lists.NextAutoName("My Playlist") == "My Playlist #1", "first auto name");
                lists.SavePlaylist(lists.NextAutoName("My Playlist"), "Pasted", null, new[] { "P - Q" }, 0, out added);
                lists.SavePlaylist(lists.NextAutoName("My Playlist"), "Pasted", null, new[] { "R - S" }, 0, out added);
                Check(lists.ByName("My Playlist #2") != null && lists.NextAutoName("My Playlist") == "My Playlist #3", "auto names count up");
                lists.Remove(lists.ByName("My Playlist #1").Id);
                Check(lists.NextAutoName("My Playlist") == "My Playlist #1", "a deleted number is reused");
                lists.SavePlaylist("Videos", "YouTube Music", null, new[] { "Some Artist - Some Song" }, 0, out added);
                Check(lists.MatchTrack("Random Channel", "Some Artist - Some Song (Official Video)") == "Videos", "video title 'Artist - Song' matches");
            });

            Test("Playlist links: recognised, and Spotify / YouTube Music pages parsed", () =>
            {
                Check(PlaylistLinks.IsPlaylistLink("https://open.spotify.com/playlist/1a2B3c4D5e6F7g8H9i0J1k?si=0000"), "spotify link");
                Check(PlaylistLinks.IsPlaylistLink("https://open.spotify.com/intl-de/album/1a2B3c4D5e6F7g8H9i0J1k"), "spotify album");
                Check(PlaylistLinks.IsPlaylistLink("https://music.youtube.com/playlist?list=PL0123456789abcdefABCDEF_-xyzXYZ"), "ytm link");
                Check(PlaylistLinks.IsPlaylistLink("https://www.youtube.com/watch?v=abc&list=PL0123456789abcdefABCDEF_-xyzXYZ&index=2"), "watch link with list");
                Check(!PlaylistLinks.IsPlaylistLink("https://example.com/list.txt") && !PlaylistLinks.Recognizes("Artist - Song"), "other text");
                Check(PlaylistLinks.Recognizes("https://open.spotify.com/track/0000000000000000000001\r\nhttps://open.spotify.com/track/0000000000000000000002"), "copied Spotify songs");
                var e = PlaylistLinks.SpotifyEntity("<html><script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":" +
                                                   "{\"name\":\"My List\",\"trackList\":[{\"title\":\"Song A\",\"subtitle\":\"Artist One\"}]}}}}}}</script></html>");
                Check(e != null && Json.Str(e, "name") == "My List", "spotify embed data");
                string json = "{\"header\":{\"musicResponsiveHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"Hard Mix\"}]},\"secondSubtitle\":{\"runs\":[{\"text\":\"1.2K views \u2022 835 tracks\"}]}}}," +
                    "\"items\":[{\"musicResponsiveListItemRenderer\":{\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Into Ecstasy\"}]}}}," +
                    "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Hard Driver\"}]}}}]}}," +
                    "{\"musicResponsiveListItemRenderer\":{\"x\":\"MUSIC_VIDEO_TYPE_UGC\",\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Coone - Faces (Official Video)\"}]}}}," +
                    "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Some Channel \u2022 2M views\"}]}}}]}}]}";
                var r = new PlaylistRead();
                PlaylistLinks.ParseYouTube(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json), r);
                Check(r.Name == "Hard Mix" && r.Total == 835, "name and count: " + r.Name + " " + r.Total);
                Check(r.Tracks.Count == 2 && r.Tracks[0] == "Hard Driver - Into Ecstasy" && r.Tracks[1] == "Coone - Faces (Official Video)", string.Join(" | ", r.Tracks));
            });

            Test("Playlist scan into a saved playlist: skips songs it has, stops when it reaches them again", () =>
            {
                var songs = new[] { "A", "B", "C", "D", "E" };
                int idx = 0; var allowed = new List<string>();
                Func<List<MediaInfo>> snap = () => new List<MediaInfo> { new MediaInfo { Aumid = "Spotify.exe", App = "spotify", Artist = "Artist", Title = songs[idx % songs.Length], State = PlayState.Playing } };
                var known = new HashSet<string>(new[] { "Artist - C", "Artist - D", "Artist - E" }.Select(SafeLists.Key));
                var sc = new PlaylistScanner(snap, aumid => { idx++; return true; }, e2 => allowed.Add(e2)) { StepMs = 0, WaitMs = 500, Known = known };
                sc.Run("spotify");
                Check(allowed.Count == 2 && allowed[1] == "track:Artist - B" && sc.Status.Contains("already saved"), "from A: " + string.Join(" | ", allowed) + " / " + sc.Status);
                idx = 2; allowed.Clear();
                sc.Run("spotify");
                Check(allowed.Count == 2 && allowed[0] == "track:Artist - A" && sc.Status.Contains("looped"), "from C: " + string.Join(" | ", allowed) + " / " + sc.Status);
            });

            Test("Risk levels choose the action: major protects, indie can warn, ignored stays alone", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings { RiskIndependent = RiskAction.Warn, RiskUnknown = RiskAction.Ignore };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.RiskLookup = (a, t) => new RiskInfo { Level = t == "Hit" ? RiskLevel.Major : t == "Indie" ? RiskLevel.Independent : RiskLevel.Unknown, Label = "L" };
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Pop Star", "Hit", PlayState.Playing) });
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True" }), "major muted: " + string.Join("; ", obs.Calls));
                Check(e.Channels[0].Risk != null && e.Channels[0].Risk.Level == RiskLevel.Major, "risk shown");
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Band", "Indie", PlayState.Playing) });
                Check(obs.Calls.Last() == "mute Music False" && e.Channels[0].Status == ChannelStatus.Warning, "indie -> warn only: " + string.Join("; ", obs.Calls) + " " + e.Channels[0].Status);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "Nobody", "Obscure", PlayState.Playing) });
                Advance(e, 3);
                Check(e.Channels[0].Status == ChannelStatus.Allowed, "unknown -> ignored: " + e.Channels[0].Status);
            });

            Test("Stream-safe lists and safe apps skip the checks entirely", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true); obs.Add("Game", "x", false, 1, true);
                var s = new Settings(); s.Lists.FromJson(null);   // free lists on
                s.Allow.Add("app:pretzel");
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, Apps = new List<string> { "spotify", "pretzel" }, Action = ActionKind.Mute });
                s.Channels.Add(new Channel { Input = "Game", Enabled = true, Action = ActionKind.Mute });
                var e = Make(obs, s);
                e.OnMedia(new List<MediaInfo> { Song("spotify", "StreamBeats", "Lofi 3", PlayState.Playing), Song("pretzel", "Anyone", "Anything", PlayState.Playing) });
                e.OnAudio("Game", new SongHit { Song = new SongInfo { Artist = "Some DJ", Title = "Drop", Label = "NCS", Source = "AudD" }, Votes = 40 });
                Check(obs.Calls.Count == 0, "nothing muted: " + string.Join("; ", obs.Calls));
            });

            Test("Allow list matches across players: (feat. X), [Radio Edit], - Remastered, first artist", () =>
            {
                var s = new Settings();
                s.Allow.Add("track:Joel Corry - Head & Heart (feat. MNEK)");
                s.Allow.Add("track:Alex Gaudino - Destination Calabria [Radio Edit] (feat. Crystal Waters)");
                s.Allow.Add("track:David Guetta & Bebe Rexha - I'm Good (Blue)");
                s.Allow.Add("track:Queen - Bohemian Rhapsody - Remastered 2011");
                Check(s.IsAllowed("Joel Corry", "Head & Heart"), "feat. in the list, not in Now Playing");
                Check(s.IsAllowed("Alex Gaudino", "Destination Calabria"), "radio edit + feat");
                Check(s.IsAllowed("David Guetta, Bebe Rexha", "I'm Good (Blue)"), "artist list punctuation");
                Check(s.IsAllowed("David Guetta", "I'm Good (Blue)"), "first artist only");
                Check(s.IsAllowed("Queen", "Bohemian Rhapsody"), "remaster suffix");
                Check(!s.IsAllowed("Joel Corry", "Sorry"), "other song by the same artist is not allowed");
                Check(!s.IsAllowed("Someone Else", "Head & Heart"), "same title by another artist is not allowed");
            });

            Test("A failed label check is not stored for good: retried after 5 minutes, never saved", () =>
            {
                var obs = new FakeObs(); obs.Add("Music", "x", false, 1, true);
                var s = new Settings { RiskMajor = RiskAction.Warn };
                s.Channels.Add(new Channel { Input = "Music", Enabled = true, App = "spotify", Action = ActionKind.Mute });
                var e = Make(obs, s);
                int calls = 0; bool online = false;
                e.RiskLookup = (a, t) => { calls++; return online ? new RiskInfo { Level = RiskLevel.Major, Label = "Republic Records" } : new RiskInfo { Level = RiskLevel.Unknown, Failed = true, Checked = DateTime.UtcNow }; };
                e.OnMedia(new List<MediaInfo> { Song("spotify", "The Weeknd", "Blinding Lights", PlayState.Playing) });
                Check(obs.Calls.SequenceEqual(new[] { "mute Music True" }), "unknown while failing -> protected: " + string.Join("; ", obs.Calls));
                string saved = File.Exists(Paths.File("labels.json")) ? File.ReadAllText(Paths.File("labels.json")) : "";
                Check(!saved.Contains("blinding lights"), "failed result not saved");
                online = true;
                e.Risks.Put("the weeknd|blinding lights", new RiskInfo { Level = RiskLevel.Unknown, Failed = true, Checked = DateTime.UtcNow.AddMinutes(-6) });
                e.OnMedia(new List<MediaInfo> { Song("spotify", "The Weeknd", "Blinding Lights", PlayState.Playing) });
                Check(calls == 2 && e.Channels[0].Risk.Level == RiskLevel.Major, "retried after 5 min: " + calls + " " + e.Channels[0].Risk.Level);
                Check(e.Channels[0].Status == ChannelStatus.Warning, "major -> warn (as configured): " + e.Channels[0].Status);
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
