import type { ReactNode } from 'react'
import { cn } from '@/lib/cn'
import { formatDuration } from '@/lib/format'
import {
  BackTenIcon,
  ExitFullscreenIcon,
  ForwardTenIcon,
  FullscreenIcon,
  PauseIcon,
  PictureInPictureIcon,
  PlayIcon,
  SettingsIcon,
  SubtitlesIcon,
  VolumeIcon,
  VolumeMutedIcon,
} from '@/ui/icons'
import { SeekBar } from './SeekBar'

export const SKIP_SECONDS = 10

export interface PlayerControlsProps {
  paused: boolean
  /** Seconds into the file. */
  position: number
  duration: number
  bufferedEnd: number
  availableEnd: number | null
  volume: number
  muted: boolean
  subtitlesAvailable: boolean
  subtitlesOn: boolean
  settingsOpen: boolean
  settingsId: string
  /** The settings panel; without one (a ticket that lists no tracks) there is no settings button. */
  settings: ReactNode | null
  fullscreen: boolean
  canPictureInPicture: boolean
  onTogglePlay: () => void
  onSeek: (seconds: number) => void
  onSkip: (deltaSeconds: number) => void
  onVolume: (volume: number) => void
  onToggleMute: () => void
  onToggleSubtitles: () => void
  onToggleSettings: () => void
  onToggleFullscreen: () => void
  onPictureInPicture: () => void
}

/** The bar along the bottom of the picture: the timeline, then transport on the left and the rest on the right. */
export function PlayerControls(props: PlayerControlsProps) {
  const known = Number.isFinite(props.duration) && props.duration > 0
  const silent = props.muted || props.volume === 0

  return (
    <div className="flex flex-col gap-1.5 bg-linear-to-t from-black/90 via-black/60 to-transparent px-3 pb-3 pt-10 sm:px-5">
      <SeekBar
        position={props.position}
        duration={props.duration}
        bufferedEnd={props.bufferedEnd}
        availableEnd={props.availableEnd}
        onSeek={props.onSeek}
      />
      <div className="flex items-center gap-1 sm:gap-2">
        <ControlButton label={props.paused ? 'Play' : 'Pause'} onClick={props.onTogglePlay} shortcut="K">
          {props.paused ? <PlayIcon className="size-6" /> : <PauseIcon className="size-6" />}
        </ControlButton>
        <ControlButton label={`Back ${SKIP_SECONDS} seconds`} onClick={() => props.onSkip(-SKIP_SECONDS)} shortcut="ArrowLeft">
          <BackTenIcon className="size-6" />
        </ControlButton>
        <ControlButton label={`Forward ${SKIP_SECONDS} seconds`} onClick={() => props.onSkip(SKIP_SECONDS)} shortcut="ArrowRight">
          <ForwardTenIcon className="size-6" />
        </ControlButton>
        <div className="group/volume flex items-center">
          <ControlButton label={silent ? 'Unmute' : 'Mute'} onClick={props.onToggleMute} shortcut="M">
            {silent ? <VolumeMutedIcon className="size-6" /> : <VolumeIcon className="size-6" />}
          </ControlButton>
          <input
            type="range"
            aria-label="Volume"
            min={0}
            max={1}
            step={0.05}
            value={props.muted ? 0 : props.volume}
            onChange={(event) => props.onVolume(Number(event.target.value))}
            className="hidden h-1 w-0 cursor-pointer accent-accent opacity-0 transition-all group-hover/volume:w-20 group-hover/volume:opacity-100 focus-visible:w-20 focus-visible:opacity-100 sm:block"
          />
        </div>
        <span className="ml-1 text-xs tabular-nums text-muted sm:text-sm">
          <span className="text-fg">{formatDuration(props.position)}</span>
          {known && <> / {formatDuration(props.duration)}</>}
        </span>

        <div className="relative ml-auto flex items-center gap-1 sm:gap-2">
          {props.subtitlesAvailable && (
            <ControlButton
              label={props.subtitlesOn ? 'Turn subtitles off' : 'Turn subtitles on'}
              onClick={props.onToggleSubtitles}
              pressed={props.subtitlesOn}
              shortcut="C"
            >
              <SubtitlesIcon className="size-6" />
            </ControlButton>
          )}
          {props.settings && (
            <ControlButton
              label="Settings"
              onClick={props.onToggleSettings}
              expanded={props.settingsOpen}
              controls={props.settingsId}
            >
              <SettingsIcon className={cn('size-6 transition-transform', props.settingsOpen && 'rotate-45')} />
            </ControlButton>
          )}
          {props.canPictureInPicture && (
            <ControlButton label="Picture in picture" onClick={props.onPictureInPicture} className="hidden sm:inline-flex">
              <PictureInPictureIcon className="size-6" />
            </ControlButton>
          )}
          <ControlButton
            label={props.fullscreen ? 'Exit full screen' : 'Full screen'}
            onClick={props.onToggleFullscreen}
            shortcut="F"
          >
            {props.fullscreen ? <ExitFullscreenIcon className="size-6" /> : <FullscreenIcon className="size-6" />}
          </ControlButton>
          {props.settingsOpen && props.settings}
        </div>
      </div>
    </div>
  )
}

const SHORTCUT_HINTS: Record<string, string> = { ArrowLeft: '←', ArrowRight: '→' }

function ControlButton({
  label,
  onClick,
  children,
  shortcut,
  pressed,
  expanded,
  controls,
  className,
}: {
  label: string
  onClick: () => void
  children: ReactNode
  /** An `aria-keyshortcuts` value, also shown in the tooltip. */
  shortcut?: string
  pressed?: boolean
  expanded?: boolean
  controls?: string
  className?: string
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      aria-pressed={pressed}
      aria-expanded={expanded}
      aria-controls={expanded ? controls : undefined}
      aria-keyshortcuts={shortcut}
      title={shortcut ? `${label} (${SHORTCUT_HINTS[shortcut] ?? shortcut.toLowerCase()})` : label}
      className={cn(
        'inline-flex size-10 items-center justify-center rounded-control text-fg/90 transition-colors hover:bg-white/10 hover:text-fg focus-visible:outline-2 focus-visible:outline-accent',
        pressed && 'text-accent hover:text-accent-strong',
        className,
      )}
    >
      {children}
    </button>
  )
}
