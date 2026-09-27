using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SongSentry
{
    public sealed class SongInfo
    {
        public string Artist, Title, Album, Label, Source;
        public bool Safe;                 // learned as safe (allowed) music; still recognised, never acted on
        public DateTime Added;
        public LandmarkIndex.Track Track;
        public string Key { get { return SongLibrary.KeyOf(Artist, Title); } }
        public string Text { get { return string.IsNullOrEmpty(Artist) ? Title : Artist + " - " + Title; } }
    }

    public sealed class SongHit
    {
        public SongInfo Song;
        public int Votes;
        public double PositionSec;        // where in the song the heard clip starts
    }

    /// The local song memory: every song identified by Now Playing, AudD or AudioTag is learned here (landmark
    /// fingerprints at its real position in the song) and from then on recognised offline, mid-song, under game noise.
    /// Stored in %LocalAppData%\SongSentry\songs.dat (8 bytes per fingerprint, ~32 KB per minute of learned music).
    public sealed class SongLibrary
    {
        public const int MinVotes = 15;   // tested: non-library audio peaked at 9-12 votes, real songs 17-370
        readonly LandmarkIndex index = new LandmarkIndex();
        readonly Dictionary<string, SongInfo> byKey = new Dictionary<string, SongInfo>();
        readonly Dictionary<int, HashSet<int>> covered = new Dictionary<int, HashSet<int>>();   // track id -> learned seconds
        readonly object gate = new object();
        bool dirty;

        static string PathOf { get { return Paths.File("songs.dat"); } }

        public static string KeyOf(string artist, string title) { return ((artist ?? "").Trim() + "\n" + (title ?? "").Trim()).ToLowerInvariant(); }

        public int Count { get { lock (gate) return byKey.Count; } }
        public long SizeBytes { get { lock (gate) return (long)index.HashCount * 8; } }

        public List<SongInfo> Songs() { lock (gate) return byKey.Values.OrderByDescending(s => s.Added).ToList(); }

        public SongInfo GetOrAdd(string artist, string title, string album, string label, string source, bool safe)
        {
            lock (gate)
            {
                SongInfo s;
                if (byKey.TryGetValue(KeyOf(artist, title), out s))
                {
                    if (string.IsNullOrEmpty(s.Label) && !string.IsNullOrEmpty(label)) { s.Label = label; dirty = true; }
                    return s;
                }
                s = new SongInfo { Artist = artist, Title = title, Album = album, Label = label, Source = source, Safe = safe, Added = DateTime.Now };
                s.Track = index.AddTrack(s.Key, s);
                byKey[s.Key] = s;
                dirty = true;
                return s;
            }
        }

        /// Learns 8 kHz mono audio that starts at `songPosSec` in the song. Only the seconds not learned yet are added.
        public int Learn(SongInfo s, short[] pcm8k, double songPosSec)
        {
            if (pcm8k.Length < AudioCapture.Rate * 2 || songPosSec < 0) return 0;
            lock (gate)
            {
                HashSet<int> have;
                if (!covered.TryGetValue(s.Track.Id, out have)) covered[s.Track.Id] = have = new HashSet<int>();
                // Only anchors with their whole pairing window inside this chunk are complete; the edges get learned
                // (and marked as learned) by the neighbouring, overlapping chunks.
                double lenSec = pcm8k.Length / (double)AudioCapture.Rate;
                double okFrom = songPosSec + LandmarkIndex.EdgeStartFrames * LandmarkIndex.FrameSec;
                double okTo = songPosSec + lenSec - LandmarkIndex.EdgeEndFrames * LandmarkIndex.FrameSec;
                if (okTo <= okFrom) return 0;
                int first = (int)Math.Ceiling(okFrom), last = (int)Math.Floor(okTo) - 1;
                int added = 0;
                int startFrame = (int)Math.Round(songPosSec / LandmarkIndex.FrameSec);
                // learn each run of not-yet-learned whole seconds
                for (int sec = first; sec <= last; sec++)
                {
                    if (have.Contains(sec)) continue;
                    int runEnd = sec;
                    while (runEnd + 1 <= last && !have.Contains(runEnd + 1)) runEnd++;
                    added += index.Learn(s.Track, pcm8k, AudioCapture.Rate, startFrame,
                                         (int)Math.Ceiling(sec / LandmarkIndex.FrameSec), (int)Math.Floor((runEnd + 1) / LandmarkIndex.FrameSec) - 1);
                    for (int k = sec; k <= runEnd; k++) have.Add(k);
                    sec = runEnd;
                }
                dirty |= added > 0;
                return added;
            }
        }

        public SongHit Query(short[] pcm8k)
        {
            lock (gate)
            {
                if (index.Tracks.Count == 0) return null;
                var m = index.Query(pcm8k, AudioCapture.Rate);
                if (m.Track == null || m.Votes < MinVotes || m.Votes < 2 * m.Second) return null;
                var s = m.Track.Tag as SongInfo;
                return s == null ? null : new SongHit { Song = s, Votes = m.Votes, PositionSec = m.OffsetSec };
            }
        }

        public void SetSafe(SongInfo s, bool safe) { lock (gate) { s.Safe = safe; dirty = true; } }

        public void Clear() { lock (gate) { index.Clear(); byKey.Clear(); covered.Clear(); dirty = true; } Save(); }

        // ------------------------------------------------------------------ persistence

        public void Save()
        {
            byte[] data;
            lock (gate)
            {
                if (!dirty) return;
                var ms = new MemoryStream();
                var w = new BinaryWriter(ms, Encoding.UTF8);
                w.Write(0x53534C42); w.Write(1);   // "SSLB", version
                var songs = index.Tracks.Select(t => t.Tag as SongInfo).ToList();
                w.Write(songs.Count);
                foreach (var s in songs)
                {
                    w.Write(s != null); if (s == null) continue;
                    w.Write(s.Artist ?? ""); w.Write(s.Title ?? ""); w.Write(s.Album ?? ""); w.Write(s.Label ?? ""); w.Write(s.Source ?? "");
                    w.Write(s.Safe); w.Write(s.Added.ToBinary()); w.Write(s.Track.Frames);
                }
                var entries = index.Entries();
                w.Write(entries.Length);
                foreach (ulong e in entries) w.Write(e);
                w.Flush();
                data = ms.ToArray();
                dirty = false;
            }
            try
            {
                string tmp = PathOf + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(PathOf)) File.Replace(tmp, PathOf, null); else File.Move(tmp, PathOf);
            }
            catch (Exception e) { Log.Write("song memory save failed: " + e.Message); lock (gate) dirty = true; }
        }

        public void Load()
        {
            if (!File.Exists(PathOf)) return;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(PathOf), Encoding.UTF8))
                {
                    if (r.ReadInt32() != 0x53534C42 || r.ReadInt32() != 1) throw new InvalidDataException("unknown format");
                    int n = r.ReadInt32();
                    var tracks = new List<LandmarkIndex.Track>();
                    var songs = new List<SongInfo>();
                    for (int i = 0; i < n; i++)
                    {
                        var t = new LandmarkIndex.Track { Id = i };
                        if (r.ReadBoolean())
                        {
                            var s = new SongInfo { Artist = r.ReadString(), Title = r.ReadString(), Album = r.ReadString(), Label = r.ReadString(), Source = r.ReadString(),
                                                   Safe = r.ReadBoolean(), Added = DateTime.FromBinary(r.ReadInt64()), Track = t };
                            t.Frames = r.ReadInt32(); t.Name = s.Key; t.Tag = s;
                            songs.Add(s);
                        }
                        tracks.Add(t);
                    }
                    int m = r.ReadInt32();
                    var entries = new ulong[m];
                    for (int i = 0; i < m; i++) entries[i] = r.ReadUInt64();
                    lock (gate)
                    {
                        index.Load(tracks, entries);
                        byKey.Clear(); covered.Clear();
                        foreach (var s in songs) byKey[s.Key] = s;
                        foreach (ulong e in entries)   // rebuild "which seconds are learned"
                        {
                            int id = (int)((e >> 16) & 0xffff), sec = (int)((e & 0xffff) * LandmarkIndex.FrameSec);
                            HashSet<int> h;
                            if (!covered.TryGetValue(id, out h)) covered[id] = h = new HashSet<int>();
                            h.Add(sec);
                        }
                    }
                }
                Log.Write("song memory: " + Count + " songs, " + SizeBytes / 1024 + " KB");
            }
            catch (Exception e) { Log.Write("song memory load failed (starting empty): " + e.Message); }
        }
    }
}
