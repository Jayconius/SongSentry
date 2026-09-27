using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SongSentry
{
    /// What the engine needs from OBS (the real connection, or a fake in LogicTest).
    public interface IObsBackend
    {
        bool Connected { get; }
        Dictionary<string, object> Request(string type, params object[] kv);
    }

    public sealed class ObsBackend : IObsBackend
    {
        readonly ObsConnection c;
        public ObsBackend(ObsConnection c) { this.c = c; }
        public bool Connected { get { return c.State == ObsState.Connected; } }
        public Dictionary<string, object> Request(string type, params object[] kv) { return c.Request(type, kv); }
    }

    public sealed class InputInfo
    {
        public string Name, Kind, Target, App, DeviceId;
        public bool HasAudio, Muted;
        public double VolumeMul = 1;
        public Dictionary<string, bool> Tracks = new Dictionary<string, bool>();
        public InputInfo Clone()
        {
            var i = (InputInfo)MemberwiseClone();
            i.Tracks = new Dictionary<string, bool>(Tracks);
            return i;
        }
    }

    public enum ChannelStatus { Off, Idle, Listening, Checking, Allowed, Protecting, Warning, Overridden, NoObs }

    public sealed class ChannelView
    {
        public string Input;
        public ChannelStatus Status;
        public string Detail;
        public MediaInfo Media;
    }

    public sealed class EventEntry
    {
        public DateTime Time;
        public string Channel, Text, Song;
        public bool Alert;
    }

    /// Runtime state of one protected channel.
    sealed class Run
    {
        public bool Active;                 // our action is applied in OBS
        public ActionKind Applied;
        public bool OrigMuted; public double OrigVolume; public Dictionary<string, bool> OrigTracks;
        public bool SetMuted; public double SetVolume; public Dictionary<string, bool> SetTracks;   // what we wrote
        public bool TouchMute, TouchVolume, TouchTracks;   // which properties we own (false after a manual change)
        public string SongKey, OverriddenKey;
        public MediaInfo Media;
        public DateTime? ClearSince;
        public SongHit Audio; public DateTime AudioAt;   // latest song recognised in this source's own audio
        public ChannelStatus Status = ChannelStatus.Idle;
        public string Detail;
    }

    /// The brain: Now Playing + OBS events in, OBS actions out. Everything runs on one worker thread (an actor), so
    /// no locking is needed inside; the UI reads snapshots and posts commands.
    public sealed class Engine : IDisposable
    {
        readonly Settings settings;
        readonly IObsBackend obs;
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly Thread thread;
        readonly Func<DateTime> clock;
        readonly Dictionary<string, InputInfo> inputs = new Dictionary<string, InputInfo>();
        readonly Dictionary<string, Run> runs = new Dictionary<string, Run>();
        readonly Dictionary<string, SongMatch> confirmed = new Dictionary<string, SongMatch>();   // media key -> MusicBrainz answer
        readonly HashSet<string> lookingUp = new HashSet<string>();
        readonly List<EventEntry> events = new List<EventEntry>();
        List<MediaInfo> media = new List<MediaInfo>();
        string streamTrack = "1", vodTrack;
        bool live, loaded;
        volatile bool disposed;

        public event Action Updated;                      // UI: something changed, repaint
        public event Action<string, string, bool> Notify; // title, text, alert -> tray balloon
        public Func<string, string, SongMatch> SongLookup = MusicBrainz.Lookup;   // replaceable in tests
        public Func<string, bool> Skipper;                // "next track" on a media session (by AUMID); set by Program
        readonly List<DateTime> skips = new List<DateTime>();
        bool skipPaused;                                  // too many skips in a row: stop until a safe song plays
        const int MaxSkipsInARow = 5;
        public bool Synchronous;                          // tests: run posted work inline

        public Engine(Settings settings, IObsBackend obs, Func<DateTime> clock)
        {
            this.settings = settings;
            this.obs = obs;
            this.clock = clock ?? (() => DateTime.UtcNow);
            thread = new Thread(Loop) { IsBackground = true, Name = "engine", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() { thread.Start(); }

        public void Post(Action a)
        {
            if (Synchronous) { a(); return; }
            if (!disposed) queue.Add(a);
        }

        void Loop()
        {
            var next = DateTime.UtcNow;
            while (!disposed)
            {
                Action a;
                int wait = Math.Max(0, (int)(next - DateTime.UtcNow).TotalMilliseconds);
                if (queue.TryTake(out a, wait))
                {
                    try { a(); } catch (Exception e) { Log.Write("engine: " + e); }
                    continue;
                }
                next = DateTime.UtcNow.AddMilliseconds(250);
                try { Tick(); } catch (Exception e) { Log.Write("engine tick: " + e); }
            }
        }

        // ================================================================== snapshots for the UI (thread-safe copies)

        readonly object snap = new object();
        List<InputInfo> snapInputs = new List<InputInfo>();
        List<ChannelView> snapChannels = new List<ChannelView>();
        List<MediaInfo> snapMedia = new List<MediaInfo>();
        List<EventEntry> snapEvents = new List<EventEntry>();
        string snapTrackInfo = "";
        bool snapLive;

        public List<InputInfo> Inputs { get { lock (snap) return snapInputs; } }
        public List<ChannelView> Channels { get { lock (snap) return snapChannels; } }
        public List<MediaInfo> Media { get { lock (snap) return snapMedia; } }
        public List<EventEntry> Events { get { lock (snap) return snapEvents; } }
        public string TrackInfo { get { lock (snap) return snapTrackInfo; } }
        public bool Live { get { lock (snap) return snapLive; } }

        void Publish()
        {
            var ins = inputs.Values.Where(i => i.HasAudio).Select(i => i.Clone()).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var chs = settings.ChannelList().Select(c =>
            {
                Run r = RunOf(c.Input);
                return new ChannelView { Input = c.Input, Status = c.Enabled ? r.Status : ChannelStatus.Off, Detail = r.Detail, Media = r.Media == null ? null : r.Media.Clone() };
            }).ToList();
            lock (snap)
            {
                snapInputs = ins;
                snapChannels = chs;
                snapMedia = media.Select(m => m.Clone()).ToList();
                snapEvents = events.ToList();
                snapTrackInfo = "stream track " + streamTrack + (vodTrack != null ? ", Twitch VOD track " + vodTrack : "");
                snapLive = live;
            }
            var h = Updated;
            if (h != null) h();
        }

        // ================================================================== inputs from outside

        public void OnMedia(List<MediaInfo> list) { Post(() => { media = list; Evaluate(); }); }

        /// From the Recognizer, every ~2 s per monitored channel: the song heard in its audio, or null.
        public void OnAudio(string input, SongHit hit)
        {
            Post(() =>
            {
                Run r = RunOf(input);
                bool changed = (hit == null) != (r.Audio == null) || (hit != null && r.Audio != null && hit.Song != r.Audio.Song);
                if (hit != null) { r.Audio = hit; r.AudioAt = clock(); }
                else if (r.Audio != null && (clock() - r.AudioAt).TotalSeconds > AudioHold) r.Audio = null;
                if (changed || hit == null) Evaluate();
            });
        }

        /// A recognised song counts as "still playing" this long after the last matching window (windows come every 2 s).
        const double AudioHold = 5;

        public static MediaInfo AsMedia(SongHit h)
        {
            return new MediaInfo { Aumid = null, App = "audio", Artist = h.Song.Artist, Title = h.Song.Title,
                                   Album = "Heard in the audio (" + h.Song.Source + ")", State = PlayState.Playing };
        }

        public void OnObsState(bool connected)
        {
            Post(() =>
            {
                if (connected) LoadFromObs();
                else
                {
                    // OBS is gone: its sources are gone too. Keep the journal so we can restore when it comes back.
                    loaded = false;
                    foreach (var r in runs.Values) { r.Active = false; r.Status = ChannelStatus.NoObs; }
                    Publish();
                }
            });
        }

        public void OnObsEvent(string type, Dictionary<string, object> d)
        {
            Post(() => HandleEvent(type, d));
        }

        // Commands from the UI
        public void SetChannel(string input, Action<Channel> change)
        {
            Post(() =>
            {
                InputInfo inf;
                inputs.TryGetValue(input, out inf);
                Channel c = settings.GetOrAdd(input, () => NewChannel(input, inf));
                bool wasEnabled = c.Enabled;
                ActionKind oldAction = c.Action; int oldDuck = c.DuckPercent; string oldApps = string.Join(",", c.Apps);
                change(c);
                settings.Save();
                Run r = RunOf(input);
                if (r.Active && (!c.Enabled || c.Action != oldAction || c.DuckPercent != oldDuck || string.Join(",", c.Apps) != oldApps))
                    Restore(c, r, !c.Enabled ? "Protection turned off" : "Settings changed");
                if (!wasEnabled && c.Enabled) r.OverriddenKey = null;
                Evaluate();
            });
        }

        public void AllowCurrent(string input, bool wholeArtist)
        {
            Post(() =>
            {
                Run r = RunOf(input);
                if (r.Media == null) return;
                string artist = (r.Media.Artist ?? "").Trim(), title = (r.Media.Title ?? "").Trim();
                string e = wholeArtist ? "artist:" + artist : "track:" + artist + " - " + title;
                lock (settings.Sync) if (!settings.Allow.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase))) settings.Allow.Add(e);
                settings.Save();
                AddEvent(input, "Marked as safe: " + Settings.AllowLabel(e), null, false);
                Evaluate();
            });
        }

        public void RemoveAllow(string entry)
        {
            Post(() => { lock (settings.Sync) settings.Allow.Remove(entry); settings.Save(); Evaluate(); });
        }

        public void AddAllow(string entry)
        {
            Post(() => { lock (settings.Sync) if (!settings.Allow.Any(x => string.Equals(x, entry, StringComparison.OrdinalIgnoreCase))) settings.Allow.Add(entry); settings.Save(); Evaluate(); });
        }

        public void Refresh() { Post(() => { if (obs.Connected) LoadFromObs(); }); }

        /// Re-run the rules (after a setting that affects them changed).
        public void Reevaluate() { Post(Evaluate); }

        public void SetHidden(string input, bool hide)
        {
            Post(() =>
            {
                lock (settings.Sync) { settings.Hidden.Remove(input); if (hide) settings.Hidden.Add(input); }
                settings.Save();
                Publish();
            });
        }

        public void SetPaused(bool paused)
        {
            Post(() => { settings.Paused = paused; settings.Save(); AddEvent("SongSentry", paused ? "Protection paused" : "Protection resumed", null, false); Evaluate(); });
        }

        /// Called on exit: put every source back the way we found it.
        public void RestoreAll(int timeoutMs)
        {
            var done = new ManualResetEventSlim(false);
            Post(() =>
            {
                foreach (var c in settings.ChannelList())
                {
                    Run r = RunOf(c.Input);
                    if (r.Active) Restore(c, r, "SongSentry closed");
                }
                done.Set();
            });
            if (!Synchronous) done.Wait(timeoutMs);
        }

        // ================================================================== OBS model

        public Channel NewChannel(string input, InputInfo inf)
        {
            var c = new Channel { Input = input };
            c.Apps = inf != null && inf.Kind == "wasapi_process_output_capture" ? Carried(inf)
                   : Carried(inf).Where(a => media.Any(m => m.App == a) || KnownMediaApps.Contains(a)).ToList();
            if (c.Apps.Count == 0) c.App = GuessApp(input);
            return c;
        }

        /// Media apps worth suggesting even when they're not playing right now.
        public static readonly HashSet<string> KnownMediaApps = new HashSet<string>
            { "spotify", "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "tidal", "applemusic", "deezer", "vlc", "itunes", "foobar2000", "musicbee", "aimp", "winamp", "amazon music", "youtube music" };

        /// Every app whose sound goes through this OBS source: the captured exe, or (device sources) the apps Windows plays
        /// through that device right now. Used for suggestions and the "also carries other sound" note.
        public static List<string> Carried(InputInfo i)
        {
            if (i == null) return new List<string>();
            if (i.Kind == "wasapi_process_output_capture") return i.App != null ? new List<string> { i.App } : new List<string>();
            if (i.Kind == "wasapi_output_capture" || i.Kind == "wasapi_input_capture")
                return AudioDevices.AppsBehind(i.DeviceId, i.Kind == "wasapi_output_capture");
            return i.App != null ? new List<string> { i.App } : new List<string>();
        }

        string GuessApp(string inputName)
        {
            string n = inputName.ToLowerInvariant();
            foreach (var m in media) if (m.App.Length > 1 && n.Contains(m.App)) return m.App;
            if (n.Contains("spotify") || n.Contains("music")) return "spotify";
            if (n.Contains("browser") || n.Contains("youtube")) return media.Select(m => m.App).FirstOrDefault(AppKey.IsBrowser) ?? "chrome";
            return "";
        }

        void LoadFromObs()
        {
            try
            {
                var fresh = new Dictionary<string, InputInfo>();
                foreach (var d in Json.Objs(obs.Request("GetInputList")["inputs"]))
                {
                    var i = new InputInfo { Name = Json.Str(d, "inputName"), Kind = Json.Str(d, "inputKind") ?? "" };
                    ReadInput(i);
                    fresh[i.Name] = i;
                }
                inputs.Clear();
                foreach (var kv in fresh) inputs[kv.Key] = kv.Value;
                ReadTracks();
                try { live = Json.Bool(obs.Request("GetStreamStatus"), "outputActive", false); } catch { }
                loaded = true;
                RecoverJournal();
                Log.Write("Loaded " + inputs.Count + " inputs (" + inputs.Values.Count(i => i.HasAudio) + " with audio), " + snapTrackInfo);
            }
            catch (Exception e) { Log.Write("LoadFromObs failed: " + e.Message); }
            Evaluate();
        }

        void ReadInput(InputInfo i)
        {
            try
            {
                var s = Json.Obj(obs.Request("GetInputSettings", "inputName", i.Name)["inputSettings"]);
                Describe(i, s);
            }
            catch { }
            try
            {
                i.Muted = Json.Bool(obs.Request("GetInputMute", "inputName", i.Name), "inputMuted", false);
                i.HasAudio = true;
                i.VolumeMul = Json.Num(obs.Request("GetInputVolume", "inputName", i.Name), "inputVolumeMul", 1);
                i.Tracks = ReadTrackMap(Json.Obj(obs.Request("GetInputAudioTracks", "inputName", i.Name)["inputAudioTracks"]));
            }
            catch (ObsRequestException) { i.HasAudio = false; }   // 604: this input has no audio
        }

        static Dictionary<string, bool> ReadTrackMap(Dictionary<string, object> d)
        {
            var t = new Dictionary<string, bool>();
            if (d != null) foreach (var kv in d) t[kv.Key] = kv.Value is bool && (bool)kv.Value;
            return t;
        }

        /// Fills Target / App / DeviceId from the source's settings. Never stores the raw settings (they can hold tokens).
        public static void Describe(InputInfo i, Dictionary<string, object> s)
        {
            string window = Json.Str(s, "window");
            switch (i.Kind)
            {
                case "wasapi_process_output_capture":
                    if (!string.IsNullOrEmpty(window))
                    {
                        string exe = window.Split(':').Last();
                        i.App = AppKey.Of(exe);
                        i.Target = "App: " + exe;
                    }
                    else i.Target = "App: (none chosen)";
                    break;
                case "wasapi_output_capture":
                case "wasapi_input_capture":
                    i.DeviceId = Json.Str(s, "device_id") ?? "default";
                    i.Target = (i.Kind == "wasapi_output_capture" ? "Output: " : "Input: ") + AudioDevices.Name(i.DeviceId);
                    break;
                case "game_capture":
                    i.Target = "Game capture" + (Json.Bool(s, "capture_audio", false) ? " (with audio)" : "");
                    if (!string.IsNullOrEmpty(window)) i.App = AppKey.Of(window.Split(':').Last());
                    break;
                case "ffmpeg_source":
                    string f = Json.Str(s, "local_file");
                    i.Target = "Media file: " + (string.IsNullOrEmpty(f) ? "(stream)" : Path.GetFileName(f));
                    break;
                case "browser_source":
                    string u = Json.Str(s, "url");
                    Uri uri;
                    i.Target = "Browser source" + (u != null && Uri.TryCreate(u, UriKind.Absolute, out uri) && !uri.IsFile ? ": " + uri.Host : "");
                    break;
                default:
                    i.Target = i.Kind;
                    break;
            }
        }

        void ReadTracks()
        {
            streamTrack = "1"; vodTrack = null;
            try
            {
                string mode = Json.Str(obs.Request("GetProfileParameter", "parameterCategory", "Output", "parameterName", "Mode"), "parameterValue");
                if (mode == "Advanced")
                {
                    string ti = Json.Str(obs.Request("GetProfileParameter", "parameterCategory", "AdvOut", "parameterName", "TrackIndex"), "parameterValue");
                    if (!string.IsNullOrEmpty(ti)) streamTrack = ti;
                    string vodOn = Json.Str(obs.Request("GetProfileParameter", "parameterCategory", "AdvOut", "parameterName", "VodTrackEnabled"), "parameterValue");
                    if (vodOn == "true")
                        vodTrack = Json.Str(obs.Request("GetProfileParameter", "parameterCategory", "AdvOut", "parameterName", "VodTrackIndex"), "parameterValue");
                }
            }
            catch (Exception e) { Log.Write("track settings: " + e.Message); }
        }

        void HandleEvent(string type, Dictionary<string, object> d)
        {
            string name = Json.Str(d, "inputName");
            InputInfo i = null;
            if (name != null) inputs.TryGetValue(name, out i);
            switch (type)
            {
                case "InputMuteStateChanged":
                    if (i == null) break;
                    i.Muted = Json.Bool(d, "inputMuted", i.Muted);
                    CheckManual(name, r => r.TouchMute && i.Muted != r.SetMuted, r => r.TouchMute = false);
                    break;
                case "InputVolumeChanged":
                    if (i == null) break;
                    i.VolumeMul = Json.Num(d, "inputVolumeMul", i.VolumeMul);
                    CheckManual(name, r => r.TouchVolume && Math.Abs(i.VolumeMul - r.SetVolume) > 0.002, r => r.TouchVolume = false);
                    break;
                case "InputAudioTracksChanged":
                    if (i == null) break;
                    i.Tracks = ReadTrackMap(Json.Obj(d["inputAudioTracks"]));
                    CheckManual(name, r => r.TouchTracks && r.SetTracks.Any(kv => { bool v; return i.Tracks.TryGetValue(kv.Key, out v) && v != kv.Value; }),
                                r => r.TouchTracks = false);
                    break;
                case "InputCreated":
                    var n = new InputInfo { Name = name, Kind = Json.Str(d, "inputKind") ?? "" };
                    ReadInput(n);
                    inputs[name] = n;
                    break;
                case "InputRemoved":
                    inputs.Remove(name);
                    runs.Remove(name);
                    break;
                case "InputNameChanged":
                    string old = Json.Str(d, "oldInputName");
                    InputInfo o;
                    if (old != null && inputs.TryGetValue(old, out o)) { inputs.Remove(old); o.Name = name; inputs[name] = o; }
                    Run rr;
                    if (old != null && runs.TryGetValue(old, out rr)) { runs.Remove(old); runs[name] = rr; }
                    Channel ch = old != null ? settings.Find(old) : null;
                    if (ch != null) ch.Input = name;
                    int hi;
                    lock (settings.Sync) { hi = old != null ? settings.Hidden.IndexOf(old) : -1; if (hi >= 0) settings.Hidden[hi] = name; }
                    if (ch != null || hi >= 0) settings.Save();
                    break;
                case "InputSettingsChanged":
                    if (i != null) Describe(i, Json.Obj(d["inputSettings"]));
                    break;
                case "StreamStateChanged":
                    live = Json.Bool(d, "outputActive", live);
                    break;
                case "CurrentProfileChanged":
                    ReadTracks();
                    break;
                case "ExitStarted":
                    Log.Write("OBS is closing");
                    break;
                default:
                    return;
            }
            Evaluate();
        }

        /// The user changed a property we manage. Stop owning it and don't fight them for the rest of this song.
        void CheckManual(string input, Func<Run, bool> changedByUser, Action<Run> release)
        {
            Run r;
            if (!runs.TryGetValue(input, out r) || !r.Active || !changedByUser(r)) return;
            release(r);
            r.OverriddenKey = r.SongKey;
            if (!r.TouchMute && !r.TouchVolume && !r.TouchTracks)
            {
                r.Active = false;
                r.ClearSince = null;
                WriteJournal();
            }
            AddEvent(input, "You changed this source by hand, so SongSentry leaves it alone until the next song", r.Media, false);
        }

        // ================================================================== the rules

        Run RunOf(string input)
        {
            Run r;
            if (!runs.TryGetValue(input, out r)) runs[input] = r = new Run();
            return r;
        }

        void Tick()
        {
            // Everything else is event-driven; the tick only matters while a "restore after N seconds" countdown runs.
            if (loaded && runs.Values.Any(r => r.ClearSince != null)) Evaluate();
        }

        public void TickForTest() { Evaluate(); }

        void Evaluate()
        {
            DateTime now = clock();
            foreach (var c in settings.ChannelList())
            {
                Run r = RunOf(c.Input);
                InputInfo inf;
                bool present = inputs.TryGetValue(c.Input, out inf) && inf.HasAudio;
                if (!obs.Connected || !loaded) { r.Status = ChannelStatus.NoObs; r.Detail = "Waiting for OBS"; continue; }
                if (!present) { r.Status = ChannelStatus.NoObs; r.Detail = "Source not found in OBS"; continue; }
                if (!c.Enabled || settings.Paused)
                {
                    r.Status = ChannelStatus.Off; r.Detail = settings.Paused && c.Enabled ? "Paused" : null;
                    if (r.Active) Restore(c, r, settings.Paused ? "protection paused" : "protection turned off");
                    continue;
                }

                // Several apps can share one source: the first linked app playing something risky wins.
                MediaInfo m = null;
                string reason = "idle";
                bool risky = false;
                foreach (var cand in media.Where(x => c.Apps.Contains(x.App) && x.State == PlayState.Playing))
                {
                    string why;
                    bool bad = IsRisky(c, cand, out why);
                    if (m == null || bad) { m = cand; reason = why; }
                    if (bad) { risky = true; break; }
                }
                // Songs recognised in the source's own audio (song memory / online services).
                if (!risky && r.Audio != null && (now - r.AudioAt).TotalSeconds <= AudioHold)
                {
                    var am = AsMedia(r.Audio);
                    bool ok = r.Audio.Song.Safe || settings.IsAllowed(r.Audio.Song.Artist, r.Audio.Song.Title, r.Audio.Song.Label);
                    if (!ok) { m = am; reason = "song"; risky = true; }
                    else if (m == null) { m = am; reason = "allowed"; }
                }
                r.Media = m;

                if (risky)
                {
                    r.ClearSince = null;
                    string key = m.Key;
                    if (r.OverriddenKey == key)
                    {
                        r.Status = ChannelStatus.Overridden; r.Detail = "Left alone: you changed it by hand during this song";
                        continue;
                    }
                    r.OverriddenKey = null;
                    if (!r.Active) { Apply(c, r, inf, m); TrySkip(c, m); }
                    else if (r.SongKey != key)
                    {
                        r.SongKey = key;
                        AddEvent(c.Input, "Next song, still protected", m, true);
                        TrySkip(c, m);
                    }
                    r.Status = c.Action == ActionKind.Warn ? ChannelStatus.Warning : ChannelStatus.Protecting;
                    r.Detail = Channel.ActionLabel(c.Action, c.DuckPercent);
                }
                else
                {
                    // "Until the track is over": a paused risky track still counts, until it ends or another track starts.
                    MediaInfo held = null;
                    if (r.Active && settings.Restore == RestoreMode.TrackEnd && r.SongKey != null)
                    {
                        held = media.FirstOrDefault(x => x.Key == r.SongKey && x.State == PlayState.Paused && !settings.IsAllowed(x.Artist, x.Title));
                        TimeSpan? left = held != null ? held.Remaining(now) : null;
                        if (left.HasValue && left.Value.TotalSeconds < 1.5) held = null;   // paused at the very end = over
                    }
                    if (held != null)
                    {
                        r.ClearSince = null;
                        r.Media = held;
                        r.Status = c.Action == ActionKind.Warn ? ChannelStatus.Warning : ChannelStatus.Protecting;
                        r.Detail = Channel.ActionLabel(c.Action, c.DuckPercent) + " (paused, held until the track is over)";
                        continue;
                    }
                    if (r.Active)
                    {
                        if (r.ClearSince == null) r.ClearSince = now;
                        if ((now - r.ClearSince.Value).TotalSeconds >= settings.RestoreDelay) Restore(c, r, reason);
                    }
                    if (m != null && (reason == "allowed" || reason == "not a song")) skipPaused = false;   // a safe song re-arms skipping
                    if (!r.Active)
                    {
                        r.SongKey = null;
                        if (m == null && r.OverriddenKey != null) r.OverriddenKey = null;   // song ended: re-arm
                    }
                    r.Status = reason == "allowed" ? ChannelStatus.Allowed : reason == "checking" ? ChannelStatus.Checking
                             : m != null ? ChannelStatus.Listening : ChannelStatus.Idle;
                    r.Detail = reason == "allowed" ? "On your allow list" : reason == "checking" ? "Checking the song on MusicBrainz…"
                             : reason == "not a song" ? "Not a known song" : m != null ? "Playing" : "Nothing playing";
                    if (r.Active) r.Detail = "Restoring shortly…";
                }
            }
            Publish();
        }

        bool IsRisky(Channel c, MediaInfo m, out string reason)
        {
            reason = "idle";
            if (m == null || c.Apps.Count == 0) return false;
            if (settings.IsAllowed(m.Artist, m.Title)) { reason = "allowed"; return false; }
            if (c.Mode == DetectMode.AnyMedia && !AppKey.IsBrowser(m.App)) { reason = "media"; return true; }

            // Browsers / "known songs only": confirm the title with MusicBrainz first.
            SongMatch sm;
            if (confirmed.TryGetValue(m.Key, out sm))
            {
                if (sm.Found)
                {
                    if (settings.IsAllowed(sm.Artist, sm.Title)) { reason = "allowed"; return false; }
                    reason = "song"; return true;
                }
                reason = "not a song"; return false;
            }
            StartLookup(m);
            reason = "checking";
            return false;
        }

        void StartLookup(MediaInfo m)
        {
            string key = m.Key;
            if (!lookingUp.Add(key)) return;
            string artist, title;
            MusicBrainz.Parse(m.Title, m.Artist, out artist, out title);
            Action work = () =>
            {
                SongMatch res = SongLookup(artist, title);
                Post(() => { confirmed[key] = res; lookingUp.Remove(key); Evaluate(); });
            };
            if (Synchronous) work();
            else ThreadPool.QueueUserWorkItem(_ => work());
        }

        /// Auto-skip: ask the player for the next track. Stops after MaxSkipsInARow within a minute (a fully licensed
        /// playlist would otherwise be skipped forever) until a safe song plays.
        void TrySkip(Channel c, MediaInfo m)
        {
            if (!settings.AutoSkip || c.Action == ActionKind.Warn || Skipper == null || m == null || m.Aumid == null || skipPaused) return;
            DateTime now = clock();
            skips.RemoveAll(t => (now - t).TotalSeconds > 60);
            if (skips.Count >= MaxSkipsInARow)
            {
                skipPaused = true;
                AddEvent(c.Input, "Skipped " + MaxSkipsInARow + " songs in a row, so auto-skip stops for now and the source just stays muted", null, true);
                return;
            }
            skips.Add(now);
            string aumid = m.Aumid;
            string song = SongText(m);
            Action work = () =>
            {
                bool ok = Skipper(aumid);
                Post(() => AddEvent(c.Input, ok ? "Skipped to the next track" : "Couldn't skip in " + AppKey.Pretty(m.App) + ", kept it muted", null, !ok));
            };
            if (Synchronous) work(); else ThreadPool.QueueUserWorkItem(_ => work());
        }

        void Apply(Channel c, Run r, InputInfo i, MediaInfo m)
        {
            r.Active = true; r.Applied = c.Action; r.SongKey = m.Key; r.ClearSince = null;
            r.TouchMute = r.TouchVolume = r.TouchTracks = false;
            r.OrigMuted = i.Muted; r.OrigVolume = i.VolumeMul; r.OrigTracks = new Dictionary<string, bool>(i.Tracks);
            try
            {
                switch (c.Action)
                {
                    case ActionKind.Mute:
                        if (!i.Muted)
                        {
                            r.SetMuted = true; r.TouchMute = true;
                            obs.Request("SetInputMute", "inputName", c.Input, "inputMuted", true);
                            i.Muted = true;
                        }
                        break;
                    case ActionKind.Duck:
                        r.SetVolume = Math.Round(i.VolumeMul * Math.Max(0, Math.Min(100, c.DuckPercent)) / 100.0, 4);
                        r.TouchVolume = true;
                        obs.Request("SetInputVolume", "inputName", c.Input, "inputVolumeMul", r.SetVolume);
                        i.VolumeMul = r.SetVolume;
                        break;
                    case ActionKind.StreamOnly:
                        r.SetTracks = new Dictionary<string, bool>();
                        foreach (string t in new[] { streamTrack, vodTrack })
                        {
                            bool on;
                            if (t != null && i.Tracks.TryGetValue(t, out on) && on) r.SetTracks[t] = false;
                        }
                        if (r.SetTracks.Count > 0)
                        {
                            r.TouchTracks = true;
                            obs.Request("SetInputAudioTracks", "inputName", c.Input, "inputAudioTracks",
                                        r.SetTracks.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
                            foreach (var kv in r.SetTracks) i.Tracks[kv.Key] = kv.Value;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Write("apply failed on " + c.Input + ": " + e.Message);
                AddEvent(c.Input, "Couldn't change the source in OBS: " + e.Message, m, true);
            }
            bool changed = r.TouchMute || r.TouchVolume || r.TouchTracks;
            if (!changed && c.Action != ActionKind.Warn)
                AddEvent(c.Input, "Already silent on stream, nothing to change", m, false);
            else
            {
                string what = c.Action == ActionKind.Warn ? "Licensed music playing (warning only)" : Channel.ActionLabel(c.Action, c.DuckPercent);
                AddEvent(c.Input, what, m, true);
                var h = Notify;
                if (h != null) h(c.Input + ": " + (c.Action == ActionKind.Warn ? "licensed music" : Short(what)), SongText(m), c.Action == ActionKind.Warn);
            }
            WriteJournal();
        }

        void Restore(Channel c, Run r, string reason)
        {
            InputInfo i;
            inputs.TryGetValue(c.Input, out i);
            try
            {
                if (r.TouchMute && i != null && i.Muted == r.SetMuted)
                {
                    obs.Request("SetInputMute", "inputName", c.Input, "inputMuted", r.OrigMuted);
                    i.Muted = r.OrigMuted;
                }
                if (r.TouchVolume && i != null && Math.Abs(i.VolumeMul - r.SetVolume) < 0.002)
                {
                    obs.Request("SetInputVolume", "inputName", c.Input, "inputVolumeMul", r.OrigVolume);
                    i.VolumeMul = r.OrigVolume;
                }
                if (r.TouchTracks && i != null)
                {
                    var back = new Dictionary<string, object>();
                    foreach (var kv in r.SetTracks)
                    {
                        bool now, orig;
                        if (i.Tracks.TryGetValue(kv.Key, out now) && now == kv.Value && r.OrigTracks.TryGetValue(kv.Key, out orig)) back[kv.Key] = orig;
                    }
                    if (back.Count > 0)
                    {
                        obs.Request("SetInputAudioTracks", "inputName", c.Input, "inputAudioTracks", back);
                        foreach (var kv in back) i.Tracks[kv.Key] = (bool)kv.Value;
                    }
                }
            }
            catch (Exception e) { Log.Write("restore failed on " + c.Input + ": " + e.Message); }
            bool touched = r.TouchMute || r.TouchVolume || r.TouchTracks;
            r.Active = false; r.TouchMute = r.TouchVolume = r.TouchTracks = false; r.ClearSince = null;
            if (touched || r.Applied == ActionKind.Warn) AddEvent(c.Input, "Restored (" + Describe(reason) + ")", null, false);
            WriteJournal();
        }

        static string Describe(string reason)
        {
            switch (reason)
            {
                case "allowed": return "song is on your allow list";
                case "not a song": return "not a known song";
                case "idle": return "music stopped";
                default: return reason;
            }
        }

        static string Short(string action)
        {
            return action.Replace("Mute on stream", "muted on stream").Replace("Mute everywhere", "muted").Replace("Turn down", "turned down");
        }

        public static string SongText(MediaInfo m)
        {
            if (m == null) return "";
            if (string.IsNullOrEmpty(m.Artist)) return m.Title;
            return m.Artist + " - " + m.Title;
        }

        void AddEvent(string channel, string text, MediaInfo m, bool alert)
        {
            events.Insert(0, new EventEntry { Time = DateTime.Now, Channel = channel, Text = text, Song = m == null ? null : SongText(m), Alert = alert });
            if (events.Count > 100) events.RemoveAt(events.Count - 1);
            Log.Write(channel + ": " + text + (m != null ? " [" + SongText(m) + "]" : ""));
        }

        // ================================================================== crash safety: restore journal

        static string JournalPath { get { return Paths.File("restore.json"); } }

        void WriteJournal()
        {
            try
            {
                var active = runs.Where(kv => kv.Value.Active).Select(kv => Json.Make(
                    "input", kv.Key,
                    "mute", kv.Value.TouchMute, "origMuted", kv.Value.OrigMuted, "setMuted", kv.Value.SetMuted,
                    "volume", kv.Value.TouchVolume, "origVolume", kv.Value.OrigVolume, "setVolume", kv.Value.SetVolume,
                    "tracks", kv.Value.TouchTracks,
                    "origTracks", kv.Value.OrigTracks == null ? null : kv.Value.OrigTracks.ToDictionary(t => t.Key, t => (object)t.Value),
                    "setTracks", kv.Value.SetTracks == null ? null : kv.Value.SetTracks.ToDictionary(t => t.Key, t => (object)t.Value))).ToList();
                if (active.Count == 0) { if (File.Exists(JournalPath)) File.Delete(JournalPath); return; }
                File.WriteAllText(JournalPath, Json.Write(active), new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Write("journal: " + e.Message); }
        }

        /// After a crash (or OBS reconnect) put back anything the journal says we still hold.
        void RecoverJournal()
        {
            if (!File.Exists(JournalPath)) return;
            try
            {
                var list = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(JournalPath));
                foreach (var j in list)
                {
                    string input = Json.Str(j, "input");
                    InputInfo i;
                    if (input == null || !inputs.TryGetValue(input, out i)) continue;
                    var r = RunOf(input);
                    r.Active = true;
                    r.TouchMute = Json.Bool(j, "mute", false); r.OrigMuted = Json.Bool(j, "origMuted", false); r.SetMuted = Json.Bool(j, "setMuted", false);
                    r.TouchVolume = Json.Bool(j, "volume", false); r.OrigVolume = Json.Num(j, "origVolume", 1); r.SetVolume = Json.Num(j, "setVolume", 1);
                    r.TouchTracks = Json.Bool(j, "tracks", false);
                    r.OrigTracks = ReadTrackMap(Json.Obj(j.ContainsKey("origTracks") ? j["origTracks"] : null));
                    r.SetTracks = ReadTrackMap(Json.Obj(j.ContainsKey("setTracks") ? j["setTracks"] : null));
                    Channel c = settings.Find(input) ?? new Channel { Input = input };
                    Restore(c, r, "recovered after restart");
                }
            }
            catch (Exception e) { Log.Write("journal recovery: " + e.Message); }
            try { File.Delete(JournalPath); } catch { }
        }

        public void Dispose()
        {
            disposed = true;
            queue.CompleteAdding();
        }
    }
}
