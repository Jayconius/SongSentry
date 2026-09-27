# SongSentry 1.1.0 Beta

*Released 2026-09-28 · Windows 10 (2004+) / 11 · OBS 28+ · single exe, nothing to install*

SongSentry keeps licensed music off your stream by recognizing songs in your OBS sources and muting them **on the
stream only**, so your recording keeps the music. It's made for every kind of streamer: game streamers, **VTubers**,
IRL streamers, show hosts, podcasters and Just Chatting creators.

> **Beta:** SongSentry reduces the risk of DMCA mutes and strikes. It can't guarantee zero. Please report anything odd
> in [Issues](https://github.com/Jayconius/SongSentry/issues).

## New in 1.1.0

### Safe music: knows what's risky, and what isn't
- **Risk levels:** every recognized song is rated by who released it, using MusicBrainz labels and their owners
  (e.g. *Republic Records → Universal*): **major label**, **independent**, **unknown**, or **stream-safe**
  (Creative Commons or on a stream-safe list). You choose protect / warn only / ignore per level. The label is
  checked once per song and cached; until then the song is protected.
- **Stream-safe lists:** built-in free lists (StreamBeats, NoCopyrightSounds, FiXT, on by default), and switches for
  paid libraries you have a license for (Epidemic Sound, Monstercat, Artlist, Soundstripe, Pretzel). There's no
  login: the switch means "I have a license for this library".
- **Safe playlists page:** your playlists are kept by name, with their song count and source, so you can switch off,
  rename or delete a whole playlist at once. Add them by:
  - pasting a **YouTube or YouTube Music playlist link** (the whole playlist, in seconds),
  - pasting a **Spotify playlist link** (Spotify shares the first 100 songs), or **songs copied from the Spotify app**
    (Ctrl+A, Ctrl+C): every song, also for private playlists and Liked Songs. Spotify rate-limits these lookups, so
    a big playlist can take several minutes,
  - **Pear Desktop** (YouTube Music) in one click, through Pear's API Server plugin,
  - scanning the playlist that's playing in any player (about 2 s per song).

  Playlists keep their own name when it can be read, otherwise they become *My Playlist #1*, *#2*…; right-click a
  playlist to rename it, see its songs or delete it. Playlists from links are checked for new songs each time
  SongSentry starts. No accounts or API keys needed.
- **Safe apps:** everything a chosen app plays (e.g. Pretzel) counts as safe.
- Songs match across players even with "(feat. X)", "[Radio Edit]" or "- Remastered" in the title, and YouTube
  video titles like "Artist - Song (Official Video)" match the song.

### Recognition

- **AcoustID built in (free, no key needed).** When SongSentry hears music start after silence, it sends a
  fingerprint of the first ~18 s to AcoustID. In testing on a real OBS setup it named the song in 0.6 s and muted
  it on stream about 21 s after the song started. The next time that song played, the song memory recognized it in
  4 s, offline.
- **Online fallbacks:** AcoustID → AudioTag → AudD. Each service is tried when the one before finds nothing, and every
  answer is learned into the song memory, so each song is only looked up once. Lookups run in the background, so other
  sources keep being checked.
- **Faster release:** a source comes back about 5 s after the music pauses or stops (was ~12 s).
- **Picks the original recording**, not a remix or mash-up that starts the same way.
- The same song from different services counts as one song ("Trouble" = "Trouble.").
- **Fixes:** the music detector now works correctly on 8 kHz audio (it under-rated song intros), and fixed an audio
  device error that could happen depending on start-up order.
- Bundles Chromaprint **fpcalc** (LGPL 2.1), so the exe is now ~4 MB. RAM is **about 40–50 MB**. See
  [THIRD_PARTY_NOTICES.md](https://github.com/Jayconius/SongSentry/blob/main/THIRD_PARTY_NOTICES.md).

## Everything in SongSentry

### Detection
- **Now Playing:** instant detection from Spotify, YouTube Music, browsers and other media apps. Browser titles are
  confirmed on MusicBrainz first, so a stream or video title doesn't count as a song.
- **Audio recognition:** listens to each protected source's real audio (per app, or its sound device, game audio
  included) and checks it every 2 s against the local **song memory**.
- **Song memory:** offline landmark fingerprinting. Recognizes a learned song in about 5 s, from the middle, even
  when the game is louder than the music (9/10 with the game 6 dB louder, 0 false alarms in 321 checks).
- **Online fallbacks:** AcoustID (free), AudioTag and AudD (your own optional keys).

### Actions in OBS
- Mute **on stream only** (keeps the recording), **mute everywhere**, **turn down**, or **warn only**.
- **Skip to the next track** automatically in media players (stops after 5 in a row).
- Give the source back **after N seconds of quiet** or **when the track is over** (with a countdown).
- Restores the original state, never unmutes what you muted, backs off on manual changes, and has a crash journal.

### App
- Channels with search, filters, sort and hidden sources; several apps per source; a warning for shared sources.
- Allow list of artists, songs and **record labels**, plus "Mark as safe".
- Tray, Pause protection, Start with Windows, auto-reconnect, no admin rights, and **~40–50 MB of RAM**.

## Known limitations
- AcoustID only recognizes a song from its start, and needs about 18 s of it. Songs joined mid-way need the song
  memory, AudioTag or AudD.
- Audio recognition needs a few seconds; Now Playing sources are instant.
- Browser and media-file sources inside OBS use Now Playing only.
- Spotify playlist links only give the first 100 songs of public playlists; paste the copied songs for the rest.
  Spotify rate-limits those lookups, so a big playlist can take several minutes (songs are remembered afterwards).
- Private YouTube playlists can't be read from a link; play them in Pear Desktop or make them Unlisted.
- The exe isn't code-signed, so Windows SmartScreen asks once (**More info → Run anyway**).

## Download
**`SongSentry.exe`** below. Run it, then follow *Getting started* in the [README](https://github.com/Jayconius/SongSentry#getting-started).

---
🤖 Made with [Claude](https://claude.com/claude-code).
