import { useCallback, useEffect, useId, useRef, useState, type PointerEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type HlsType from 'hls.js'
import { playbackApi } from '@/api/endpoints'
import type { PlaybackPreferences, PlaybackTicket } from '@/api/types'
import { mediaUrl } from '@/lib/api'
import { cn } from '@/lib/cn'
import { secondsToTicks, ticksToSeconds } from '@/lib/format'
import { tokenStore } from '@/lib/token'
import { PlayIcon } from '@/ui/icons'
import { Spinner } from '@/ui/Spinner'
import { PlayerControls } from './PlayerControls'
import { PlayerSettings, SUBTITLE_SIZES, type SubtitleSize } from './PlayerSettings'
import { SubtitleOverlay } from './SubtitleOverlay'
import { usePlayerKeys } from './usePlayerKeys'
import { seekableEnd, useVideoState } from './useVideoState'

const PROGRESS_INTERVAL_MS = 10_000
const HIDE_CONTROLS_MS = 3_000
/**
 * How far past what a conversion has produced a seek may land and still wait for it, rather than start
 * the conversion again from there.
 */
const WAIT_FOR_CONVERSION_SECONDS = 30
const SUBTITLE_SIZE_KEY = 'cinomni.player.subtitleSize'

function readSubtitleSize(): SubtitleSize {
  try {
    const stored = localStorage.getItem(SUBTITLE_SIZE_KEY)
    return SUBTITLE_SIZES.find((size) => size === stored) ?? 'medium'
  } catch {
    return 'medium'
  }
}

function writeSubtitleSize(size: SubtitleSize): void {
  try {
    localStorage.setItem(SUBTITLE_SIZE_KEY, size)
  } catch {
    // Blocked storage only costs remembering the size.
  }
}

interface IosVideo extends HTMLVideoElement {
  webkitEnterFullscreen?: () => void
}

/**
 * The picture and everything over it. Times the viewer sees are the file's: a conversion that started
 * part way through (the ticket's stream offset) plays from its own zero, so every position read off the
 * element is shifted back before it is shown, reported or compared with a subtitle cue.
 */
export function VideoStage({
  ticket,
  onPlaybackError,
  onRestart,
}: {
  ticket: PlaybackTicket
  onPlaybackError: () => void
  /** Opens a new session with these preferences — an audio or quality change, or a seek past the conversion. */
  onRestart: (preferences: PlaybackPreferences) => void
}) {
  const containerRef = useRef<HTMLDivElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)
  const queryClient = useQueryClient()
  const settingsId = useId()
  const video = useVideoState(videoRef)
  // Read through a ref so a new callback identity does not tear down and re-attach the stream.
  const failRef = useRef(onPlaybackError)
  failRef.current = onPlaybackError

  const media = ticket.media ?? null
  const offset = ticksToSeconds(media?.streamOffsetTicks ?? 0)
  const duration = media?.durationTicks
    ? ticksToSeconds(media.durationTicks)
    : Number.isFinite(video.duration)
      ? offset + video.duration
      : Number.NaN
  const position = offset + video.currentTime
  const positionRef = useRef(position)
  positionRef.current = position
  // Where a seek is waiting for the stream to reach it (stream time): the resume point, or a seek just
  // past what a conversion has produced.
  const pendingSeek = useRef<number | null>(null)

  const displayable = media?.subtitleTracks.filter((t) => t.url !== null) ?? []
  const burnable = media?.subtitleTracks.filter((t) => t.url === null && t.canBurnIn) ?? []
  // A picture subtitle this stream carries in its video is "on" like any other, just not drawn here.
  const burnedIn = media?.burnedInSubtitle ?? null
  const initialSubtitle =
    burnedIn ?? (displayable.some((t) => t.index === ticket.selection.subtitle) ? ticket.selection.subtitle : null)
  const [subtitle, setSubtitle] = useState<number | null>(initialSubtitle)
  const lastSubtitle = useRef<number | null>(
    initialSubtitle ?? displayable.find((t) => t.isDefault)?.index ?? displayable[0]?.index ?? burnable[0]?.index ?? null,
  )
  const [subtitleSize, setSubtitleSize] = useState<SubtitleSize>(readSubtitleSize)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [controlsShown, setControlsShown] = useState(true)
  const [fullscreen, setFullscreen] = useState(false)
  const hideTimer = useRef<number | undefined>(undefined)

  const applyPendingSeek = useCallback(() => {
    const element = videoRef.current
    const target = pendingSeek.current
    if (!element || target === null) return
    const ready = ticket.method === 'DirectPlay' ? Number.isFinite(element.duration) : seekableEnd(element) >= target
    if (!ready) return
    pendingSeek.current = null
    if (ticket.method !== 'DirectPlay' || target < element.duration - 5) element.currentTime = target
  }, [ticket.method])

  // Attach the source: native file for Direct Play, hls.js (with bearer auth) for HLS. hls.js is a
  // large dependency, so it is loaded lazily and only when a session actually needs it.
  useEffect(() => {
    const element = videoRef.current
    if (!element) return

    let destroyed = false
    let hls: HlsType | null = null
    const resume = ticksToSeconds(ticket.resumePositionTicks) - offset
    pendingSeek.current = resume > 1 ? resume : null

    const onLoaded = () => {
      applyPendingSeek()
      void element.play().catch(() => undefined)
    }
    element.addEventListener('loadedmetadata', onLoaded)
    element.addEventListener('durationchange', applyPendingSeek)
    element.addEventListener('progress', applyPendingSeek)

    const playViaQueryToken = () => {
      // Direct Play, or native HLS (Safari): authenticate via the access_token query param.
      element.src = mediaUrl(ticket.streamUrl)
    }

    if (ticket.method === 'DirectPlay') {
      playViaQueryToken()
    } else {
      void import('hls.js').then(({ default: Hls }) => {
        if (destroyed) return
        if (!Hls.isSupported()) {
          playViaQueryToken()
          return
        }
        hls = new Hls({
          xhrSetup: (xhr) => {
            const token = tokenStore.get()
            if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)
          },
        })
        // hls.js reports what it cannot recover from here, not on the <video> element: without this a
        // manifest that 404s or a segment that stops arriving left a black player and no way out. A
        // decode error and a network error each get one recovery attempt, which is what hls.js offers
        // for them; a second, or anything else fatal, ends on the "Playback stopped" screen.
        let recoveredMedia = false
        let reloadedNetwork = false
        hls.on(Hls.Events.ERROR, (_event, data) => {
          if (!data.fatal) return
          if (data.type === Hls.ErrorTypes.MEDIA_ERROR && !recoveredMedia) {
            recoveredMedia = true
            hls?.recoverMediaError()
            return
          }
          if (data.type === Hls.ErrorTypes.NETWORK_ERROR && !reloadedNetwork) {
            reloadedNetwork = true
            hls?.startLoad()
            return
          }
          failRef.current()
        })
        hls.loadSource(ticket.streamUrl)
        hls.attachMedia(element)
      })
    }

    return () => {
      destroyed = true
      element.removeEventListener('loadedmetadata', onLoaded)
      element.removeEventListener('durationchange', applyPendingSeek)
      element.removeEventListener('progress', applyPendingSeek)
      hls?.destroy()
    }
  }, [ticket, offset, applyPendingSeek])

  // Report progress on a cadence and on pause, and end the session on leave. Positions are the file's.
  const durationRef = useRef(duration)
  durationRef.current = duration
  useEffect(() => {
    const element = videoRef.current
    if (!element) return

    const report = (): Promise<unknown> => {
      if (!Number.isFinite(durationRef.current)) return Promise.resolve()
      return playbackApi
        .progress(ticket.sessionId, {
          positionTicks: secondsToTicks(positionRef.current),
          durationTicks: secondsToTicks(durationRef.current),
          isPaused: element.paused,
        })
        .catch(() => undefined)
    }
    const reportNow = () => void report()

    const timer = window.setInterval(reportNow, PROGRESS_INTERVAL_MS)
    element.addEventListener('pause', reportNow)

    // Not sendBeacon: a beacon cannot carry the Authorization header, so the token went in the URL.
    const stopBeacon = () => playbackApi.stopOnUnload(ticket.sessionId)
    window.addEventListener('pagehide', stopBeacon)

    return () => {
      window.clearInterval(timer)
      element.removeEventListener('pause', reportNow)
      window.removeEventListener('pagehide', stopBeacon)
      const lastReport = report()
      void playbackApi.stop(ticket.sessionId).catch(() => undefined)
      // Where the viewer lands next (Home, the title's page) reads resume positions: once the final
      // position is recorded, those reads are out of date.
      void lastReport.then(() =>
        Promise.all([
          queryClient.invalidateQueries({ queryKey: ['playback', 'in-progress'] }),
          queryClient.invalidateQueries({ queryKey: ['playback', 'progress'] }),
        ]),
      )
    }
  }, [ticket.sessionId, queryClient])

  useEffect(() => {
    const onChange = () => setFullscreen(document.fullscreenElement === containerRef.current)
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  // The controls fade away while the film plays untouched, and come back on any movement.
  const revealControls = useCallback(() => {
    setControlsShown(true)
    window.clearTimeout(hideTimer.current)
    hideTimer.current = window.setTimeout(() => setControlsShown(false), HIDE_CONTROLS_MS)
  }, [])
  useEffect(() => () => window.clearTimeout(hideTimer.current), [])
  // Playback starting (or resuming) begins the countdown too, so untouched controls do not stay up.
  useEffect(() => {
    if (!video.paused) revealControls()
  }, [video.paused, revealControls])
  const controlsVisible = controlsShown || video.paused || settingsOpen

  const currentPreferences = (): PlaybackPreferences => ({
    audioStreamIndex: ticket.selection.audio ?? undefined,
    quality: media?.quality,
    ...(subtitle === null ? { subtitlesOff: true } : { subtitleStreamIndex: subtitle }),
    startPositionTicks: secondsToTicks(positionRef.current),
  })

  const togglePlay = () => {
    const element = videoRef.current
    if (!element) return
    if (element.paused) void element.play().catch(() => undefined)
    else element.pause()
  }

  const seekTo = (target: number) => {
    const element = videoRef.current
    if (!element) return
    const file = Math.max(0, Number.isFinite(duration) ? Math.min(target, duration - 1) : target)
    const local = file - offset
    if (ticket.method === 'DirectPlay') {
      element.currentTime = Math.max(0, local)
      return
    }
    const reached = seekableEnd(element)
    // A conversion only reaches as far as it has got. Just ahead of that, wait for it; further ahead or
    // behind where it started, start it again from there.
    if (ticket.method === 'Transcode' && (local < 0 || local > reached + WAIT_FOR_CONVERSION_SECONDS)) {
      onRestart({ ...currentPreferences(), startPositionTicks: secondsToTicks(file) })
      return
    }
    if (local > reached) {
      pendingSeek.current = local
      element.currentTime = Math.max(0, reached - 1)
      return
    }
    element.currentTime = Math.max(0, local)
  }

  const setVolume = (volume: number) => {
    const element = videoRef.current
    if (!element) return
    element.volume = Math.min(1, Math.max(0, volume))
    element.muted = element.volume === 0
  }

  const toggleMute = () => {
    const element = videoRef.current
    if (!element) return
    element.muted = !element.muted
    if (!element.muted && element.volume === 0) element.volume = 0.5
  }

  const chooseSubtitle = (index: number | null) => {
    const track = media?.subtitleTracks.find((t) => t.index === index)
    // A picture track is drawn into the video by the server, and one already drawn in can only be taken
    // out the same way: both need a new stream, from where the viewer is.
    if ((track && track.url === null && track.canBurnIn) || (burnedIn !== null && index !== burnedIn)) {
      onRestart({
        ...currentPreferences(),
        ...(index === null ? { subtitlesOff: true, subtitleStreamIndex: undefined } : { subtitlesOff: undefined, subtitleStreamIndex: index }),
      })
      return
    }
    setSubtitle(index)
    if (index !== null) lastSubtitle.current = index
    // Remembered for the next time this title plays; a failure only costs that memory.
    void playbackApi.chooseSubtitle(ticket.sessionId, index).catch(() => undefined)
  }

  const toggleSubtitles = () => chooseSubtitle(subtitle === null ? lastSubtitle.current : null)

  const toggleFullscreen = () => {
    const container = containerRef.current
    const element: IosVideo | null = videoRef.current
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => undefined)
    } else if (container?.requestFullscreen) {
      void container.requestFullscreen().catch(() => undefined)
    } else {
      // iOS Safari only lets the video element itself go full screen, with its own controls.
      element?.webkitEnterFullscreen?.()
    }
  }

  const canPictureInPicture = typeof document !== 'undefined' && document.pictureInPictureEnabled === true
  const togglePictureInPicture = () => {
    if (document.pictureInPictureElement) void document.exitPictureInPicture().catch(() => undefined)
    else void videoRef.current?.requestPictureInPicture().catch(() => undefined)
  }

  usePlayerKeys({
    onTogglePlay: togglePlay,
    onSkip: (delta) => seekTo(positionRef.current + delta),
    onVolume: (delta) => setVolume((videoRef.current?.volume ?? 1) + delta),
    onToggleMute: toggleMute,
    onToggleFullscreen: toggleFullscreen,
    onToggleSubtitles: displayable.length + burnable.length > 0 ? toggleSubtitles : undefined,
    onEscape: settingsOpen ? () => setSettingsOpen(false) : undefined,
    onActivity: revealControls,
  })

  const onStagePointerUp = (event: PointerEvent<HTMLDivElement>) => {
    if (event.target !== videoRef.current) return
    // A tap on a touch screen brings the controls back; a click with a mouse pauses or plays.
    if (event.pointerType === 'mouse') togglePlay()
    else if (controlsVisible && !video.paused) setControlsShown(false)
    else revealControls()
    if (settingsOpen) setSettingsOpen(false)
  }

  const subtitleUrl = displayable.find((t) => t.index === subtitle)?.url ?? null
  const availableEnd =
    ticket.method !== 'DirectPlay' && Number.isFinite(video.duration) && offset + video.duration < duration - 1
      ? offset + video.duration
      : null

  return (
    <div
      ref={containerRef}
      className={cn('relative flex h-full w-full items-center justify-center bg-black', !controlsVisible && 'cursor-none')}
      onPointerMove={revealControls}
      onPointerUp={onStagePointerUp}
      onDoubleClick={(event) => {
        if (event.target === videoRef.current) toggleFullscreen()
      }}
    >
      <video
        ref={videoRef}
        autoPlay
        playsInline
        onError={onPlaybackError}
        className="max-h-full w-full bg-black"
        aria-label="Video player"
      />

      {media && <SubtitleOverlay url={subtitleUrl} time={position} size={subtitleSize} raised={controlsVisible} />}

      {video.waiting && !video.paused ? (
        <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
          <Spinner className="size-12 text-fg/80" />
        </div>
      ) : (
        video.paused &&
        controlsVisible && (
          <button
            type="button"
            onClick={togglePlay}
            aria-label="Play"
            className="absolute left-1/2 top-1/2 inline-flex size-18 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full bg-black/55 text-fg backdrop-blur transition-colors hover:bg-accent hover:text-on-accent focus-visible:outline-2 focus-visible:outline-accent"
          >
            <PlayIcon className="size-9 translate-x-0.5" />
          </button>
        )
      )}

      <div
        className={cn(
          'absolute inset-x-0 bottom-0 transition-opacity duration-300 motion-reduce:transition-none',
          controlsVisible ? 'opacity-100' : 'pointer-events-none opacity-0',
        )}
        onFocusCapture={revealControls}
      >
        <PlayerControls
          paused={video.paused}
          position={position}
          duration={duration}
          bufferedEnd={offset + video.bufferedEnd}
          availableEnd={availableEnd}
          volume={video.volume}
          muted={video.muted}
          subtitlesAvailable={displayable.length + burnable.length > 0}
          subtitlesOn={subtitle !== null}
          settingsOpen={settingsOpen}
          settingsId={settingsId}
          fullscreen={fullscreen}
          canPictureInPicture={canPictureInPicture}
          onTogglePlay={togglePlay}
          onSeek={seekTo}
          onSkip={(delta) => seekTo(position + delta)}
          onVolume={setVolume}
          onToggleMute={toggleMute}
          onToggleSubtitles={toggleSubtitles}
          onToggleSettings={() => setSettingsOpen((open) => !open)}
          onToggleFullscreen={toggleFullscreen}
          onPictureInPicture={togglePictureInPicture}
          settings={
            media ? (
              <PlayerSettings
                id={settingsId}
                media={media}
                audio={ticket.selection.audio}
                subtitle={subtitle}
                rate={video.playbackRate}
                subtitleSize={subtitleSize}
                onAudio={(index) => onRestart({ ...currentPreferences(), audioStreamIndex: index })}
                onSubtitle={chooseSubtitle}
                onQuality={(id) => onRestart({ ...currentPreferences(), quality: id })}
                onRate={(rate) => {
                  if (videoRef.current) videoRef.current.playbackRate = rate
                }}
                onSubtitleSize={(size) => {
                  setSubtitleSize(size)
                  writeSubtitleSize(size)
                }}
              />
            ) : null
          }
        />
      </div>
    </div>
  )
}
