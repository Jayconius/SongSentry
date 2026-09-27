using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SongSentry
{
    public enum ActionKind { StreamOnly, Mute, Duck, Warn }

    public enum DetectMode { AnyMedia, KnownSongs }

    /// When to give a source back: a fixed quiet time, or (media players) only once the risky track is over.
    public enum RestoreMode { AfterQuiet, TrackEnd }

    public sealed class Channel
    {
        public string Input;            // OBS input name
        public bool Enabled;
        public List<string> Apps = new List<string>();   // AppKeys of the media apps this source carries ("spotify", "brave", ...)

        /// Single-app shortcut (older settings files, tests): get = first app, set = exactly this one app.
        public string App
        {
            get { return Apps.Count > 0 ? Apps[0] : ""; }
            set { Apps = string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value }; }
        }

        public string AppsLabel
        {
            get
            {
                if (Apps.Count == 0) return "";
                if (Apps.Count <= 2) return string.Join(" + ", Apps.Select(AppKey.Pretty));
                return AppKey.Pretty(Apps[0]) + " + " + (Apps.Count - 1) + " more";
            }
        }
        public ActionKind Action = ActionKind.StreamOnly;
        public int DuckPercent = 20;    // volume while ducked, % of the original
        public DetectMode Mode = DetectMode.AnyMedia;

        public static string ActionLabel(ActionKind a, int duck)
        {
            switch (a)
            {
                case ActionKind.StreamOnly: return "Mute on stream";
                case ActionKind.Mute: return "Mute everywhere";
                case ActionKind.Duck: return "Turn down to " + duck + "%";
                default: return "Warn only";
            }
        }
    }

    public sealed class Settings
    {
        public string Host = "127.0.0.1";
        public int Port = 4455;
        public string Password = "";
        public bool CloseToTray = true;
        public bool StartHidden;
        public bool Paused;                     // global kill switch: nothing gets changed in OBS
        public double RestoreDelay = 2.0;       // seconds of "no risky song" before restoring a source
        public RestoreMode Restore = RestoreMode.AfterQuiet;
        public bool AutoSkip;                   // send "next track" to the player when a risky song starts
        public bool ListenToAudio = true;       // recognise songs in the channels' actual audio (song memory + online keys)
        public string AudDKey = "", AudioTagKey = "";   // optional, user-supplied (stored encrypted); never shipped with a key
        public List<Channel> Channels = new List<Channel>();
        public List<string> Hidden = new List<string>();  // OBS input names hidden from the Channels list
        public List<string> Allow = new List<string>();   // "artist:<name>" or "track:<artist> - <title>" (any case)

        static string PathOf { get { return Paths.File("settings.json"); } }

        /// Guards Channels / Allow / Hidden and the settings file: the UI thread and the engine thread both use them.
        public readonly object Sync = new object();

        public Channel Find(string input)
        {
            lock (Sync) return Channels.FirstOrDefault(c => c.Input == input);
        }

        public Channel[] ChannelList()
        {
            lock (Sync) return Channels.ToArray();
        }

        public Channel GetOrAdd(string input, Func<Channel> make)
        {
            lock (Sync)
            {
                var c = Channels.FirstOrDefault(x => x.Input == input);
                if (c == null) { c = make(); Channels.Add(c); }
                return c;
            }
        }

        public List<string> AllowList() { lock (Sync) return Allow.ToList(); }
        public List<string> HiddenList() { lock (Sync) return Hidden.ToList(); }

        public bool IsAllowed(string artist, string title) { return IsAllowed(artist, title, null); }

        public bool IsAllowed(string artist, string title, string label)
        {
            string lb = (label ?? "").Trim().ToLowerInvariant();
            string a = (artist ?? "").Trim().ToLowerInvariant(), t = (title ?? "").Trim().ToLowerInvariant();
            foreach (string raw in Allow)
            {
                string e = raw.ToLowerInvariant();
                if (e.StartsWith("artist:") && a.Length > 0 && ArtistMatches(a, e.Substring(7))) return true;
                if (e.StartsWith("track:") && e.Substring(6) == a + " - " + t) return true;
                if (e.StartsWith("label:") && lb.Length > 0 && (lb == e.Substring(6) || lb.StartsWith(e.Substring(6) + " ") || lb.StartsWith(e.Substring(6) + ","))) return true;
            }
            return false;
        }

        // "StreamBeats" allows "StreamBeats", "StreamBeats, Harris Heller" and "Harris Heller & StreamBeats".
        static bool ArtistMatches(string artist, string allowed)
        {
            if (artist == allowed) return true;
            foreach (string part in artist.Split(new[] { ",", "&", " feat. ", " ft. ", " x ", ";" }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Trim() == allowed) return true;
            return false;
        }

        public static string AllowLabel(string e)
        {
            if (e.StartsWith("artist:")) return "Artist  " + e.Substring(7);
            if (e.StartsWith("track:")) return "Song  " + e.Substring(6);
            if (e.StartsWith("label:")) return "Label  " + e.Substring(6);
            return e;
        }

        // ------------------------------------------------------------------ persistence

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(PathOf)) return s;
                var d = Json.Read(File.ReadAllText(PathOf, Encoding.UTF8));
                s.Host = Json.Str(d, "host") ?? s.Host;
                s.Port = (int)Json.Num(d, "port", s.Port);
                s.Password = Unprotect(Json.Str(d, "password"));
                s.CloseToTray = Json.Bool(d, "closeToTray", true);
                s.StartHidden = Json.Bool(d, "startHidden", false);
                s.Paused = Json.Bool(d, "paused", false);
                s.RestoreDelay = Math.Max(0, Math.Min(120, Json.Num(d, "restoreDelay", 2.0)));
                s.Restore = ParseEnum(Json.Str(d, "restore"), RestoreMode.AfterQuiet);
                s.AutoSkip = Json.Bool(d, "autoSkip", false);
                s.ListenToAudio = Json.Bool(d, "listenToAudio", true);
                s.AudDKey = Unprotect(Json.Str(d, "auddKey"));
                s.AudioTagKey = Unprotect(Json.Str(d, "audioTagKey"));
                foreach (var c in Json.Objs(d.ContainsKey("channels") ? d["channels"] : null))
                {
                    var ch = new Channel();
                    ch.Input = Json.Str(c, "input");
                    ch.Enabled = Json.Bool(c, "enabled", false);
                    object apps;
                    if (c.TryGetValue("apps", out apps) && apps is System.Collections.ArrayList)
                    { foreach (object o in (System.Collections.ArrayList)apps) if (o is string && !ch.Apps.Contains((string)o)) ch.Apps.Add((string)o); }
                    else ch.App = Json.Str(c, "app") ?? "";   // settings from 0.1.0 had a single app
                    ch.Action = ParseEnum(Json.Str(c, "action"), ActionKind.StreamOnly);
                    ch.DuckPercent = (int)Json.Num(c, "duck", 20);
                    ch.Mode = ParseEnum(Json.Str(c, "mode"), DetectMode.AnyMedia);
                    if (!string.IsNullOrEmpty(ch.Input)) s.Channels.Add(ch);
                }
                object hidden;
                if (d.TryGetValue("hidden", out hidden) && hidden is System.Collections.ArrayList)
                    foreach (object o in (System.Collections.ArrayList)hidden) if (o is string) s.Hidden.Add((string)o);
                object allow;
                if (d.TryGetValue("allow", out allow) && allow is System.Collections.ArrayList)
                    foreach (object o in (System.Collections.ArrayList)allow) if (o is string) s.Allow.Add((string)o);
            }
            catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
            return s;
        }

        public void Save()
        {
            lock (Sync)
            try
            {
                var d = Json.Make("host", Host, "port", Port, "password", Protect(Password), "closeToTray", CloseToTray,
                    "startHidden", StartHidden, "paused", Paused, "restoreDelay", RestoreDelay, "restore", Restore.ToString(), "autoSkip", AutoSkip, "listenToAudio", ListenToAudio,
                    "auddKey", Protect(AudDKey), "audioTagKey", Protect(AudioTagKey), "allow", Allow, "hidden", Hidden,
                    "channels", Channels.Select(c => Json.Make("input", c.Input, "enabled", c.Enabled, "apps", c.Apps,
                        "action", c.Action.ToString(), "duck", c.DuckPercent, "mode", c.Mode.ToString())).ToList());
                string tmp = PathOf + ".tmp";
                File.WriteAllText(tmp, Json.Write(d), new UTF8Encoding(false));
                if (File.Exists(PathOf)) File.Replace(tmp, PathOf, null);
                else File.Move(tmp, PathOf);
            }
            catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
        }

        static T ParseEnum<T>(string s, T def) where T : struct
        {
            T v;
            return s != null && Enum.TryParse(s, out v) ? v : def;
        }

        // The OBS password is stored encrypted for the current Windows user (DPAPI).
        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] b = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(b);
        }

        static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        /// Reads host/port/password from OBS's own obs-websocket config (only valid once OBS has saved it).
        public static bool TryReadObsConfig(out int port, out string password, out bool enabled)
        {
            port = 4455; password = ""; enabled = false;
            try
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                        @"obs-studio\plugin_config\obs-websocket\config.json");
                if (!File.Exists(p)) return false;
                var d = Json.Read(File.ReadAllText(p));
                port = (int)Json.Num(d, "server_port", 4455);
                enabled = Json.Bool(d, "server_enabled", false);
                password = Json.Bool(d, "auth_required", false) ? (Json.Str(d, "server_password") ?? "") : "";
                return true;
            }
            catch { return false; }
        }
    }
}
