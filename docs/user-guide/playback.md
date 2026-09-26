# Watching

Press **Play** on a movie or an episode. The video plays in the browser, full width, with Cinomni's
own controls.

## Picking up where you left off

Your position is saved as you watch, per account, and **Home → Continue watching** takes you back to
it. The audio and subtitle tracks you chose are remembered per title, so the next episode starts with
the same language.

## The controls

Move the mouse or tap the video to bring the controls back. **Settings** (the gear) opens:

| Setting | Choices |
|---|---|
| **Audio** | Every audio track in the file, by language and format. |
| **Subtitles** | Off, the subtitles inside the file, and the subtitles Cinomni downloaded; plus the text size. |
| **Quality** | The original, or a lower quality for a slow connection (which converts the video). |
| **Speed** | Slower or faster playback. |

Picture-in-picture and full screen are next to the gear.

## Keyboard shortcuts

| Key | Action |
|---|---|
| **Space** or **K** | Play / pause |
| **←** / **J** | Back 10 seconds |
| **→** / **L** | Forward 10 seconds |
| **↑** / **↓** | Volume up / down |
| **M** | Mute |
| **C** | Subtitles on / off |
| **F** | Full screen |
| **Esc** | Close the settings |

## Direct Play, Remux and Transcode

Before a video starts, Cinomni compares the file with what your browser says it can play, and picks
one of three ways to deliver it. The badge at the top of the player names the method; select it to
see **why**.

| Method | What happens | Cost to the server |
|---|---|---|
| **DirectPlay** | The file is sent as it is. | Almost nothing. |
| **Remux** | The video and audio are kept as they are but repackaged into a format the browser accepts. | Low. |
| **Transcode** | The video (and possibly the audio) is converted on the fly. | High: CPU or GPU for as long as you watch. |

Common reasons for a transcode: a video codec your browser does not play (HEVC in some browsers, for
example), a picture subtitle you turned on, a quality lower than the original, or a limit the
administrator set on resolution or bitrate.

To avoid one: choose the original quality, prefer text subtitles, or use a browser that plays the
codec (the *why* panel names it).

## When playback will not start

- **"The server is converting as many streams as it can right now"** or **"You already have as many
  streams being converted as your account allows"**: the server limits how many videos it converts at
  once, in total and per account. Direct Play is never limited. Stop another stream, or try again
  later.
- **Buffering on a 4K or HDR film**: the server may not convert it fast enough. Ask your
  administrator; see [Transcoding and hardware](../administration/transcoding.md).
- **Colours look washed out**: an HDR film is being converted without tone mapping. Ask your
  administrator.

A stream that nobody watches for a few minutes (a closed tab, for instance) is stopped by the server.
Pressing play again starts a new one from where you were.
