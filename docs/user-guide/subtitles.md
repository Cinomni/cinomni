# Subtitles

## Where subtitles come from

A video can have subtitles from two places:

- **Inside the file.** Many releases carry subtitle tracks. Cinomni lists them in the player as they
  are.
- **Downloaded by Cinomni.** After a file is imported, Cinomni searches the subtitle providers for
  every *wanted language* and saves the best match next to the video. If nothing is found yet, it
  searches again later on its own.

The providers are **OpenSubtitles** and **SubDL**; each works once the administrator has configured
its API key. Without either, downloaded subtitles are simply not available and everything else works.

A title's page shows, for each file, the subtitle searches Cinomni made and their outcome.

## Choosing subtitles in the player

Open **Settings → Subtitles** in the player, or press **C** to turn them on and off. The text size can
be changed in the same menu.

There are two kinds of subtitle track:

| Kind | Examples | What happens |
|---|---|---|
| **Text** | SRT, ASS, WebVTT | Shown by the player over the video. Never causes a conversion. |
| **Picture** | PGS (Blu-ray), VobSub (DVD) | Browsers cannot draw them, so the server burns them into the video. This forces a **transcode**, which costs server resources and counts against the conversion limit. |

When both exist, prefer a text track.

## For administrators

Under **Console → Settings → Subtitles**:

| Setting | Meaning |
|---|---|
| Wanted languages | Two-letter codes in search order, for example `en, es`. Empty fetches none. A change also applies to titles already in the library. |
| Hearing-impaired only | On: only a subtitle with sound descriptions counts for a language. Off: such a subtitle does not count. |
| Forced only | On: only a forced subtitle (just the foreign-language lines) counts. Off: a forced one does not. |

The OpenSubtitles key is `OPENSUBTITLES_API_KEY` in `.env`. The SubDL key is the
`Subtitles__Subdl__ApiKey` setting in the `cinomni` service's environment. Whether picture subtitles
may be burned in is **Burn in picture subtitles** under **Console → Settings → Playback**.
