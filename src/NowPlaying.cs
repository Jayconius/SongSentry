using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Media.Control;

namespace SongSentry
{
    public enum PlayState { Playing, Paused, Stopped, Other }

    public sealed class MediaInfo
    {
        public string Aumid;       // raw SourceAppUserModelId
        public string App;         // normalised AppKey
        public string Title, Artist, Album;
        public PlayState State;
        public TimeSpan Duration;
        public TimeSpan Position;      // as reported at PositionAt (players only report now and then)
        public DateTime PositionAt;    // UTC
        public bool CanSkip;

        /// Best estimate of the time left in the track, or null when the player doesn't report a timeline.
        public TimeSpan? Remaining(DateTime utcNow)
        {
            if (Duration <= TimeSpan.Zero) return null;
            TimeSpan pos = Position;
            if (State == PlayState.Playing && PositionAt > DateTime.MinValue) pos += utcNow - PositionAt;
            TimeSpan left = Duration - pos;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        public string Key { get { return App + "|" + Artist + "|" + Title; } }

        public MediaInfo Clone() { return (MediaInfo)MemberwiseClone(); }
    }

    /// Watches Windows' media sessions (GSMTC: the same data as the media-key volume flyout).
    /// Built without System.Runtime.WindowsRuntime: WinRT async operations are awaited via their Completed callback.
    public sealed class NowPlayingWatcher : IDisposable
    {
        GlobalSystemMediaTransportControlsSessionManager mgr;
        readonly List<GlobalSystemMediaTransportControlsSession> hooked = new List<GlobalSystemMediaTransportControlsSession>();
        readonly object gate = new object();
        Dictionary<string, MediaInfo> current = new Dictionary<string, MediaInfo>();
        Timer poll;
        int refreshing;

        /// Raised (on a worker thread) whenever the set of sessions or any track / play state changes.
        public event Action<List<MediaInfo>> Changed;

        public bool Available { get; private set; }

        public void Start()
        {
            try
            {
                mgr = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                mgr.SessionsChanged += (s, e) => Refresh();
                Available = true;
            }
            catch (Exception e)
            {
                Log.Write("Now Playing unavailable: " + e.Message);
                return;
            }
            Refresh();
            poll = new Timer(_ => Refresh(), null, 2000, 2000);   // safety net: browsers don't always raise events
        }

        /// Asks the player to skip to its next track. Returns false if the app doesn't allow it.
        public bool SkipNext(string aumid)
        {
            try
            {
                if (mgr == null) return false;
                foreach (var s in mgr.GetSessions())
                    if (s.SourceAppUserModelId == aumid) return Await(s.TrySkipNextAsync());
            }
            catch (Exception e) { Log.Write("skip failed: " + e.Message); }
            return false;
        }

        public List<MediaInfo> Snapshot()
        {
            lock (gate) return current.Values.Select(m => m.Clone()).ToList();
        }

        void Refresh()
        {
            if (mgr == null || Interlocked.Exchange(ref refreshing, 1) == 1) return;
            try
            {
                var next = new Dictionary<string, MediaInfo>();
                foreach (var s in mgr.GetSessions())
                {
                    Hook(s);
                    var m = Read(s);
                    if (m != null) next[m.Aumid] = m;
                }
                bool changed;
                lock (gate)
                {
                    changed = next.Count != current.Count || next.Any(kv =>
                    {
                        MediaInfo old;
                        return !current.TryGetValue(kv.Key, out old) || old.Key != kv.Value.Key || old.State != kv.Value.State
                               || old.PositionAt != kv.Value.PositionAt || old.Duration != kv.Value.Duration;
                    });
                    current = next;
                }
                if (changed)
                {
                    var h = Changed;
                    if (h != null) h(Snapshot());
                }
            }
            catch (Exception e) { Log.Write("Now Playing refresh: " + e.Message); }
            finally { Interlocked.Exchange(ref refreshing, 0); }
        }

        void Hook(GlobalSystemMediaTransportControlsSession s)
        {
            lock (hooked)
            {
                if (hooked.Any(h => h.SourceAppUserModelId == s.SourceAppUserModelId && ReferenceEquals(h, s))) return;
                hooked.RemoveAll(h => h.SourceAppUserModelId == s.SourceAppUserModelId);
                hooked.Add(s);
            }
            s.MediaPropertiesChanged += (x, e) => Refresh();
            s.PlaybackInfoChanged += (x, e) => Refresh();
        }

        static MediaInfo Read(GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                var m = new MediaInfo { Aumid = s.SourceAppUserModelId, App = AppKey.Of(s.SourceAppUserModelId) };
                var p = Await(s.TryGetMediaPropertiesAsync());
                if (p != null) { m.Title = p.Title ?? ""; m.Artist = p.Artist ?? ""; m.Album = p.AlbumTitle ?? ""; }
                var pb = s.GetPlaybackInfo();
                switch (pb.PlaybackStatus)
                {
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing: m.State = PlayState.Playing; break;
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused: m.State = PlayState.Paused; break;
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed: m.State = PlayState.Stopped; break;
                    default: m.State = PlayState.Other; break;
                }
                var tl = s.GetTimelineProperties();
                m.Duration = tl.EndTime - tl.StartTime;
                m.Position = tl.Position - tl.StartTime;
                m.PositionAt = tl.LastUpdatedTime.UtcDateTime;
                m.CanSkip = pb.Controls != null && pb.Controls.IsNextEnabled;
                return m;
            }
            catch { return null; }
        }

        static T Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var done = new ManualResetEvent(false);
            op.Completed = (o, st) => done.Set();
            if (op.Status == Windows.Foundation.AsyncStatus.Started && !done.WaitOne(3000)) throw new TimeoutException("WinRT call timed out");
            if (op.Status == Windows.Foundation.AsyncStatus.Error) throw op.ErrorCode;
            return op.GetResults();
        }

        public void Dispose()
        {
            if (poll != null) poll.Dispose();
        }
    }
}
