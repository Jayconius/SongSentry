# SongSentry 1.0.0 Beta

*Released 2026-09-27 · Windows 10 (2004+) / 11 · OBS 28+ · single exe, nothing to install*

The first public beta of **SongSentry**. It keeps licensed music off your stream by recognizing songs in your OBS
sources and muting them **on the stream only**, so your recording keeps the music.

> **Beta:** SongSentry reduces the risk of DMCA mutes and strikes. It can't guarantee zero. Please report anything odd
> in [Issues](https://github.com/Jayconius/SongSentry/issues).

## What's in 1.0.0

### Detection
- **Now Playing:** instant detection of the current track from Spotify, YouTube Music, browsers and other media
  apps (Windows media controls). Browser titles are confirmed on MusicBrainz first, so a stream or video title
  doesn't count as a song.
- **Audio recognition:** listens to each protected source's real audio (per app, or its sound device, game audio
  included) and checks it every 2 s against a local **song memory**.
- **Song memory:** offline landmark fingerprinting, written from scratch. Recognizes a learned song in about 5 s,
  from the middle of the song, even when the game is louder than the music. In testing: 9/10 with the game 6 dB louder,
  and 0 false alarms in 321 checks of game audio, speech and radio chatter.
- **Learning:** every identified song is learned at its real position while it plays. About 32 KB per minute of music.
- **Optional online recognition** with *your own* key: **AudioTag.info** (free every month) and **AudD** (300 free).
  Only short clips of unknown music are sent, and every answer is learned so each song is looked up once.

### Actions in OBS (obs-websocket v5)
- Mute **on stream only** (removes the source from the stream track and the Twitch VOD track, keeps the recording),
  **mute everywhere**, **turn down** to 5–50 %, or **warn only**.
- **Skip to the next track** automatically in media players (stops after 5 skips in a row).
- Give the source back **after N seconds of quiet** or **when the track is over** (with a countdown).
- Restores the original mute, volume and tracks. It never unmutes something you muted, and backs off when you change a
  source by hand.
- **Crash journal:** if SongSentry is closed unexpectedly, it restores the sources the next time it starts.

### App
- **Channels:** search, filters (in use, per-app, devices, media and other, shared), sort, hide sources, several apps per
  source (auto-detected from Windows audio sessions), and a warning for shared sources.
- **Allow list:** artists, songs and **record labels**, plus one-click "Mark as safe".
- Tray icon with notifications, **Pause protection**, **Start with Windows**, and automatic reconnection to OBS.
- A new dark UI, DPI-aware, about 220 KB, no admin rights needed.
- Privacy: settings, keys (DPAPI-encrypted), song memory and a redacted log stay in `%LocalAppData%\SongSentry`.

## Known limitations
- Songs the song memory hasn't learned yet are only recognized through Now Playing or an online key.
- Audio recognition needs about 5 s; Now Playing sources are instant.
- Browser sources inside OBS and media-file sources use Now Playing only (their audio isn't captured).
- AudioTag needs clips of at least 12 s; neither online service recognizes music the game drowns out.
- The exe isn't code-signed, so Windows SmartScreen asks once (**More info → Run anyway**).

## Download
**`SongSentry.exe`** below. Run it, then follow *Getting started* in the [README](https://github.com/Jayconius/SongSentry#getting-started).

---
🤖 Made with [Claude](https://claude.com/claude-code).
