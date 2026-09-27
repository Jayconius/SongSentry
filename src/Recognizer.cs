using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SongSentry
{
    /// Listens to every protected channel's real audio and names the song in it:
    ///   1. song memory (offline landmark matcher, ~7 ms per check),
    ///   2. if that doesn't know it and it sounds like music: AudioTag / AudD with the user's own key (rate-limited),
    ///   3. every identified song is learned into the memory at its real position, and keeps being learned while it plays
    ///      (Now Playing songs too, where the player reports the position), so next time it's recognised offline.
    public sealed class Recognizer : IDisposable
    {
        const int TickMs = 2000;
        const double Window = 5.0;                  // seconds per memory check
        const double SilenceRms = 0.003;            // below this a window counts as silence
        const int OnlineCooldownMiss = 30, OnlineCooldownHit = 20;   // seconds per channel between online lookups
        const double FollowMaxSeconds = 600;        // keep learning an identified song for at most 10 minutes

        readonly Settings settings;
        readonly Engine engine;
        public readonly SongLibrary Library;
        readonly Dictionary<string, Monitor> mons = new Dictionary<string, Monitor>();
        readonly Thread thread;
        volatile bool running = true;
        DateTime lastSave = DateTime.UtcNow, lastOnlineGlobal = DateTime.MinValue;

        sealed class Monitor
        {
            public string Input, Kind, Key;          // Key = what we capture ("pid:123" / "dev:..."), to notice changes
            public AudioCapture Cap;
            public string State = "Starting…";
            public DateTime RetryAt, NextOnline, SilentSince = DateTime.MaxValue;
            public int MusicStreak;
            public SongHit LastHit; public DateTime LastHitAt;
            public SongInfo Follow; public double FollowPos; public DateTime FollowAt, FollowStarted; public double FollowLearnedTo;
        }

        public Recognizer(Settings settings, Engine engine, SongLibrary library)
        {
            this.settings = settings; this.engine = engine; Library = library;
            thread = new Thread(Loop) { IsBackground = true, Name = "recognizer", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() { thread.Start(); }

        /// What the recognizer is doing on a channel, for the UI ("Listening to Spotify (app)", "App not running", ...).
        public string StateOf(string input)
        {
            lock (mons) { Monitor m; return mons.TryGetValue(input, out m) ? m.State : null; }
        }

        /// Diagnostics: the last song-memory match on a channel (song, votes, where in the song the window started).
        public SongHit LastHit(string input, out DateTime at)
        {
            lock (mons) { Monitor m; at = DateTime.MinValue; if (!mons.TryGetValue(input, out m)) return null; at = m.LastHitAt; return m.LastHit; }
        }

        void Loop()
        {
            while (running)
            {
                var started = DateTime.UtcNow;
                try { Tick(); } catch (Exception e) { Log.Write("recognizer: " + e); }
                if ((DateTime.UtcNow - lastSave).TotalSeconds > 60) { Library.Save(); lastSave = DateTime.UtcNow; }
                int wait = TickMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        void Tick()
        {
            var inputs = engine.Inputs.ToDictionary(i => i.Name);
            var want = new HashSet<string>();
            if (settings.ListenToAudio && !settings.Paused)
                foreach (var c in settings.ChannelList().Where(c => c.Enabled))
                    if (inputs.ContainsKey(c.Input)) want.Add(c.Input);

            // stop monitors that are no longer wanted
            lock (mons)
                foreach (var gone in mons.Keys.Where(k => !want.Contains(k)).ToList())
                {
                    if (mons[gone].Cap != null) mons[gone].Cap.Dispose();
                    mons.Remove(gone);
                    engine.OnAudio(gone, null);
                }

            var media = engine.Media;
            foreach (string name in want)
            {
                Monitor m;
                lock (mons) if (!mons.TryGetValue(name, out m)) mons[name] = m = new Monitor { Input = name };
                var inf = inputs[name];
                if (!EnsureCapture(m, inf)) { engine.OnAudio(name, null); continue; }
                var ch = settings.Find(name);
                Check(m, ch, media);
            }
        }

        // ------------------------------------------------------------------ capture management

        bool EnsureCapture(Monitor m, InputInfo inf)
        {
            string key = null, label = null;
            bool output = false;
            string app = null;
            switch (inf.Kind)
            {
                case "wasapi_process_output_capture":
                case "game_capture":
                    app = inf.App;
                    if (app == null) { m.State = inf.Kind == "game_capture" ? "Game capture: no game chosen" : "No app chosen in OBS"; return false; }
                    break;
                case "wasapi_output_capture": output = true; key = "out:" + (inf.DeviceId ?? "default"); break;
                case "wasapi_input_capture": key = "in:" + (inf.DeviceId ?? "default"); break;
                default:
                    m.State = "Can't listen to this source type (Now Playing only)";
                    Drop(m);
                    return false;
            }
            if (app != null)
            {
                if (m.Cap != null && m.Kind == "pid" && ProcessAlive(m.Cap.ProcessId) && m.Key == "app:" + app) return m.Cap.Alive || Restart(m);
                if (DateTime.UtcNow < m.RetryAt) return false;
                int pid = AudioCapture.RootProcess(app);
                if (pid == 0) { Drop(m); m.State = AppKey.Pretty(app) + " isn't running"; m.RetryAt = DateTime.UtcNow.AddSeconds(5); return false; }
                key = "app:" + app; label = AppKey.Pretty(app);
                return Open(m, "pid", key, () => AudioCapture.ForProcess(pid, label), "Listening to " + label);
            }
            if (m.Cap != null && m.Key == key) return m.Cap.Alive || Restart(m);
            if (DateTime.UtcNow < m.RetryAt) return false;
            return Open(m, "dev", key, () => AudioCapture.ForDevice(inf.DeviceId, output), "Listening to " + AudioDevices.Name(inf.DeviceId));
        }

        bool Open(Monitor m, string kind, string key, Func<AudioCapture> make, string state)
        {
            Drop(m);
            try
            {
                m.Cap = make();
                m.Cap.Start();
                m.Kind = kind; m.Key = key; m.State = state;
                Log.Write("listening to " + m.Input + ": " + m.Cap.Description);
                return true;
            }
            catch (Exception e)
            {
                m.State = "Can't capture: " + e.Message;
                m.RetryAt = DateTime.UtcNow.AddSeconds(10);
                Log.Write("capture " + m.Input + " failed: " + e.Message);
                Drop(m);
                return false;
            }
        }

        bool Restart(Monitor m) { m.RetryAt = DateTime.MinValue; Drop(m); return false; }

        static void Drop(Monitor m) { if (m.Cap != null) { m.Cap.Dispose(); m.Cap = null; m.Key = null; } }

        static bool ProcessAlive(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; } catch { return false; }
        }

        // ------------------------------------------------------------------ recognition

        void Check(Monitor m, Channel ch, List<MediaInfo> media)
        {
            var now = DateTime.UtcNow;
            short[] clip = m.Cap.Last(Window);
            if (clip.Length < AudioCapture.Rate * 3) { engine.OnAudio(m.Input, null); return; }
            if (Rms(clip) < SilenceRms)
            {
                if (m.SilentSince == DateTime.MaxValue) m.SilentSince = now;
                if ((now - m.SilentSince).TotalSeconds > 3) m.Follow = null;   // song over
                m.MusicStreak = 0;
                engine.OnAudio(m.Input, null);
                return;
            }
            m.SilentSince = DateTime.MaxValue;

            // 1. song memory
            SongHit hit = Library.Query(clip);
            if (hit != null)
            {
                m.LastHit = hit; m.LastHitAt = now;
                engine.OnAudio(m.Input, hit);
                if (m.Follow != hit.Song) StartFollow(m, hit.Song, hit.PositionSec + Window, now);   // position at the end of the window
                KeepLearning(m, now);
                m.MusicStreak = 0;
                return;
            }

            // 2. Now Playing on this channel (the player reports the song and position): learn it for later
            MediaInfo np = ch == null ? null : media.FirstOrDefault(x => ch.Apps.Contains(x.App) && x.State == PlayState.Playing
                                                                      && !string.IsNullOrEmpty(x.Title) && x.Duration.TotalSeconds > 20);
            if (np != null && !AppKey.IsBrowser(np.App))
            {
                TimeSpan? left = np.Remaining(now);
                if (left.HasValue)
                {
                    var song = Library.GetOrAdd(np.Artist, np.Title, np.Album, null, "Now Playing", settings.IsAllowed(np.Artist, np.Title));
                    double posEnd = (np.Duration - left.Value).TotalSeconds;
                    if (m.Follow != song) StartFollow(m, song, posEnd, now);
                    KeepLearning(m, now);
                }
            }
            else if (m.Follow != null)
            {
                // Self-check: after 15 s of learning, the memory must recognise what we follow. If it doesn't, the music
                // changed (e.g. a DJ mix with no gap) - stop, so we never learn other audio under this song's name.
                if ((now - m.FollowStarted).TotalSeconds > 15) m.Follow = null;
                else KeepLearning(m, now);
            }

            engine.OnAudio(m.Input, m.Follow != null && m.Follow.Source != "Now Playing"
                ? new SongHit { Song = m.Follow, Votes = 0, PositionSec = m.FollowPos } : null);

            // 3. unknown music: ask an online service with the user's own key
            bool music = MusicDetector.LooksLikeMusic(clip, AudioCapture.Rate);
            m.MusicStreak = music ? m.MusicStreak + 1 : 0;
            bool haveKey = !string.IsNullOrWhiteSpace(settings.AudioTagKey) || !string.IsNullOrWhiteSpace(settings.AudDKey);
            if (m.MusicStreak >= 2 && haveKey && m.Follow == null && np == null && now >= m.NextOnline && (now - lastOnlineGlobal).TotalSeconds >= 8)
                AskOnline(m, now);
        }

        void AskOnline(Monitor m, DateTime now)
        {
            lastOnlineGlobal = now;
            AudDResult r = null; string source = null;
            short[] clip = m.Cap.Last(13);
            if (!string.IsNullOrWhiteSpace(settings.AudioTagKey) && clip.Length >= AudioCapture.Rate * AudioTag.MinSeconds)
            {
                r = AudioTag.Recognize(settings.AudioTagKey, clip, AudioCapture.Rate);
                source = "AudioTag";
                if (!r.Ok) Log.Write("AudioTag: " + r.Error);
            }
            if ((r == null || !r.Found) && !string.IsNullOrWhiteSpace(settings.AudDKey))
            {
                clip = m.Cap.Last(10);
                r = AudD.Recognize(settings.AudDKey, clip, AudioCapture.Rate);
                source = "AudD";
                if (!r.Ok) Log.Write("AudD: " + r.Error);
            }
            var after = DateTime.UtcNow;
            if (r == null || !r.Found)
            {
                m.NextOnline = after.AddSeconds(OnlineCooldownMiss);
                return;
            }
            m.NextOnline = after.AddSeconds(OnlineCooldownHit);
            var song = Library.GetOrAdd(r.Artist, r.Title, r.Album, r.Label, source, settings.IsAllowed(r.Artist, r.Title, r.Label));
            // Where in the song did the clip start? AudD reports it ("mm:ss"); AudioTag doesn't, so park that audio past
            // anything learned so far - it still teaches the memory this stretch of the song.
            double pos;
            if (!TryTimecode(r.Timecode, out pos)) pos = song.Track.Frames * LandmarkIndex.FrameSec + 30;
            Library.Learn(song, clip, pos);
            double endPos = pos + clip.Length / (double)AudioCapture.Rate + (after - now).TotalSeconds;
            StartFollow(m, song, endPos, after);
            m.FollowLearnedTo = endPos;
            engine.OnAudio(m.Input, new SongHit { Song = song, PositionSec = endPos });
            Log.Write(m.Input + ": " + source + " identified " + song.Text + (string.IsNullOrEmpty(r.Label) ? "" : " [" + r.Label + "]"));
        }

        void StartFollow(Monitor m, SongInfo s, double posNow, DateTime now)
        {
            m.Follow = s; m.FollowPos = posNow; m.FollowAt = now; m.FollowStarted = now; m.FollowLearnedTo = posNow - Window;
        }

        /// Learn the new audio since the last call at its position in the followed song.
        void KeepLearning(Monitor m, DateTime now)
        {
            if (m.Follow == null) return;
            if ((now - m.FollowStarted).TotalSeconds > FollowMaxSeconds) { m.Follow = null; return; }
            double posNow = m.FollowPos + (now - m.FollowAt).TotalSeconds;
            // Learn in ~10 s pieces with 2 s overlap: tested as good as learning the whole song at once (short pieces lose
            // too much at their edges, where peaks lack pairing partners).
            if (posNow - m.FollowLearnedTo < 8) return;
            double from = Math.Max(m.FollowLearnedTo - 2, posNow - 12);
            short[] pcm = m.Cap.Last(posNow - from);
            Library.Learn(m.Follow, pcm, posNow - pcm.Length / (double)AudioCapture.Rate);
            m.FollowLearnedTo = posNow;
        }

        static bool TryTimecode(string tc, out double sec)
        {
            sec = 0;
            if (string.IsNullOrEmpty(tc)) return false;
            var p = tc.Split(':');
            int a, b, c;
            if (p.Length == 2 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b)) { sec = a * 60 + b; return true; }
            if (p.Length == 3 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b) && int.TryParse(p[2], out c)) { sec = a * 3600 + b * 60 + c; return true; }
            return false;
        }

        static double Rms(short[] x)
        {
            double t = 0;
            foreach (short s in x) t += (double)s * s;
            return Math.Sqrt(t / Math.Max(1, x.Length)) / 32768;
        }

        public void Dispose()
        {
            running = false;
            thread.Join(3000);
            lock (mons) foreach (var m in mons.Values) Drop(m);
            Library.Save();
        }
    }
}
