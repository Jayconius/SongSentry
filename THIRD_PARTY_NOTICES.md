# Third-party notices

SongSentry's own code is under the [MIT license](LICENSE). It bundles one third-party program:

## fpcalc (Chromaprint 1.6.1)

- **What:** `fpcalc.exe`, the audio fingerprinting tool of [Chromaprint](https://github.com/acoustid/chromaprint), used
  unmodified to create the fingerprints that SongSentry sends to [AcoustID](https://acoustid.org).
- **Where:** it is embedded in `SongSentry.exe` and unpacked on first use to `%LocalAppData%\SongSentry\fpcalc-1.6.1.exe`,
  where it runs as a separate program. You may replace that file with your own build of fpcalc.
- **Copyright:** Chromaprint © 2010–2016 Lukáš Lalinský. The Windows build includes parts of
  [FFmpeg](https://ffmpeg.org) (© the FFmpeg developers).
- **License:** Chromaprint's own code is MIT; because it includes FFmpeg code, fpcalc as a whole is licensed under the
  **GNU Lesser General Public License version 2.1**. Full texts:
  - [third_party/chromaprint/LICENSE.md](third_party/chromaprint/LICENSE.md) (Chromaprint's license statement)
  - [third_party/chromaprint/COPYING.LGPLv2.1.txt](third_party/chromaprint/COPYING.LGPLv2.1.txt) (LGPL 2.1)
- **Source code:** the exact version bundled is the official release
  [Chromaprint v1.6.1](https://github.com/acoustid/chromaprint/releases/tag/v1.6.1)
  (`chromaprint-fpcalc-1.6.1-windows-x86_64.zip`, fpcalc.exe SHA-256
  `00dcc56d911f2dea84737aa9dc8e2d118c9eb7a037d815d1ed001d8593e8fbee`). Source:
  https://github.com/acoustid/chromaprint/tree/v1.6.1 · FFmpeg source: https://ffmpeg.org/download.html

## Online services (not bundled)

SongSentry talks to these services; their own terms apply:
- [AcoustID](https://acoustid.org): song identification from fingerprints (free for non-commercial use)
- [MusicBrainz](https://musicbrainz.org): song data
- [AudioTag.info](https://audiotag.info) and [AudD](https://audd.io): optional, only with your own API key
