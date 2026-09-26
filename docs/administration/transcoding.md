# Transcoding and hardware

## When Cinomni converts a video

For every playback, Cinomni picks one of three methods and records why (viewers see it in the player;
see [Watching](../user-guide/playback.md#direct-play-remux-and-transcode)):

- **Direct Play**: the file as it is. Almost free.
- **Remux**: same video and audio, repackaged. Cheap.
- **Transcode**: converted to HLS on the fly with FFmpeg. Expensive.

A transcode happens when the browser cannot play the video codec, when a picture subtitle is burned
in, when the viewer picks a lower quality, or when the file is above the server's **Maximum
resolution** or **Maximum bitrate**. HDR tone mapping never causes a transcode on its own; it applies
when one is happening anyway.

## Limits

| `.env` variable | Default | Meaning |
|---|---|---|
| `CINOMNI_MAX_TRANSCODES` | 4 | Conversions (transcodes and remuxes) running at once on the server. |
| `CINOMNI_MAX_TRANSCODES_PER_ACCOUNT` | 2 | The same, per account. |
| `CINOMNI_CPUS` / `CINOMNI_MEMORY` | `4.0` / `2g` | Resources for the whole application container. |

A request above a limit is refused with a message saying which one; Direct Play is never limited. On
the default 4 CPUs without a GPU, 2 simultaneous 1080p transcodes is a realistic limit.

A conversion stops when its viewer stops, when nobody has asked for it for five minutes, or after six
hours at most.

## Using a GPU

The standard image converts on the CPU. An overlay adds a GPU:

| Server | GPU | Overlay | Backend |
|---|---|---|---|
| Linux | Intel or AMD | `docker-compose.hwaccel.yml` | VAAPI, or Quick Sync on Intel |
| Linux | NVIDIA | `docker-compose.nvidia.yml` | NVENC (decode with NVDEC) |
| Windows / macOS with Docker Desktop | any | none | CPU only: Docker Desktop cannot pass the GPU through |

### Intel or AMD

1. Find the host's `render` group id: `getent group render`.
2. Set it in `.env`: `CINOMNI_RENDER_GID=<id>`.
3. Start with the overlay:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.hwaccel.yml up -d
   ```

If the server has more than one GPU, point `Playback__HardwareAcceleration__DevicePath` at the
right `/dev/dri/renderD…` node in the `cinomni` service's environment.

### NVIDIA

1. Install and configure the NVIDIA Container Toolkit on the host, and check that Docker sees the GPU:
   `docker run --rm --gpus all ubuntu:24.04 nvidia-smi`.
2. Start with the overlay:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d
   ```

## The hardware test

Applying an overlay makes a GPU *available*; Cinomni only uses it after testing it. At every start,
and whenever you press **Run hardware test**, each possible backend must really encode (and then
decode) a few frames. Only what passes is used; anything else falls back to the CPU, never to a failed
start or a failed playback.

**Console → Settings → Playback → Transcoding hardware** shows:

- each backend that passed, with the codecs it encodes and decodes;
- whether HDR tone mapping, subtitle burn-in and software HEVC are available;
- **Show test details**: every test that ran, and FFmpeg's own error for each failure.

Run the test again after installing or updating a driver; no restart is needed.

| What you see | Likely cause |
|---|---|
| No tests listed | The GPU device never reached the container: no overlay, wrong device path, or (NVIDIA) no GPU granted. |
| Encode failed with a permission error | `CINOMNI_RENDER_GID` is missing or wrong. |
| Only HEVC encode failed | The GPU encodes H.264 but not HEVC; common on older GPUs. H.264 still uses it. |
| Only a decode failed | The GPU still encodes; decoding that codec is done on the CPU. |

## 4K and HDR

- Converting 4K HDR on the CPU is slower than real time with the default resources. Give the container
  8–16 CPUs and about 6 GB (`CINOMNI_CPUS`, `CINOMNI_MEMORY`), or use a GPU.
- Tone mapping brings HDR10, HLG and Dolby Vision (profiles 7 and 8) to SDR when converting. Dolby
  Vision profile 5 is not tone-mapped correctly yet and can show a green or purple cast.
- A device that plays the file directly receives it untouched, so an HDR screen shows real HDR.

## Everything else

Every setting, the exact test procedure, running the server natively on Windows for a GPU, and what
has and has not been verified on real hardware are in
[DEPLOYMENT.md §9](../../DEPLOYMENT.md#9-transcoding-and-hardware-acceleration).
