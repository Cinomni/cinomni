import { useState, type PointerEvent } from 'react'
import { formatDuration } from '@/lib/format'

/**
 * The timeline: a native range input for the keyboard and assistive tech, drawn over a track that
 * shows what is played, what is buffered, and — while the server converts — how far it has got.
 * Dragging only previews; the seek happens when the pointer lets go, so scrubbing through a converted
 * stream does not restart the conversion at every step.
 */
export function SeekBar({
  position,
  duration,
  bufferedEnd,
  availableEnd,
  onSeek,
}: {
  /** All in seconds into the file. */
  position: number
  duration: number
  bufferedEnd: number
  /** How far the stream reaches, when less than the whole file (a conversion in progress); else null. */
  availableEnd: number | null
  onSeek: (seconds: number) => void
}) {
  const [scrub, setScrub] = useState<number | null>(null)
  const [hover, setHover] = useState<{ x: number; time: number } | null>(null)
  const known = Number.isFinite(duration) && duration > 0
  const shown = scrub ?? position
  const share = (seconds: number) => (known ? `${Math.min(100, Math.max(0, (seconds / duration) * 100))}%` : '0%')

  const onPointerMove = (event: PointerEvent<HTMLInputElement>) => {
    if (!known) return
    const rect = event.currentTarget.getBoundingClientRect()
    const fraction = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width))
    setHover({ x: fraction * rect.width, time: fraction * duration })
  }

  const commit = () => {
    if (scrub !== null) onSeek(scrub)
    setScrub(null)
  }

  return (
    <div className="group/seek relative flex h-5 items-center">
      <div className="pointer-events-none absolute inset-x-0 h-1 rounded-full bg-white/20 transition-[height] group-hover/seek:h-1.5">
        {availableEnd !== null && (
          <div className="absolute inset-y-0 left-0 rounded-full bg-white/15" style={{ width: share(availableEnd) }} />
        )}
        <div className="absolute inset-y-0 left-0 rounded-full bg-white/35" style={{ width: share(bufferedEnd) }} />
        <div className="absolute inset-y-0 left-0 rounded-full bg-accent" style={{ width: share(shown) }} />
        <div
          className="absolute top-1/2 size-3.5 -translate-x-1/2 -translate-y-1/2 scale-0 rounded-full bg-accent-strong shadow transition-transform group-hover/seek:scale-100 group-has-[:focus-visible]/seek:scale-100"
          style={{ left: share(shown) }}
        />
      </div>
      {hover && (
        <span
          className="pointer-events-none absolute bottom-full mb-2 -translate-x-1/2 rounded-md bg-black/85 px-1.5 py-0.5 text-xs tabular-nums text-fg"
          style={{ left: hover.x }}
        >
          {formatDuration(hover.time)}
        </span>
      )}
      <input
        type="range"
        aria-label="Seek"
        min={0}
        max={known ? Math.floor(duration) : 0}
        step={1}
        value={Math.floor(shown)}
        disabled={!known}
        aria-valuetext={`${formatDuration(shown)} of ${known ? formatDuration(duration) : 'unknown'}`}
        onChange={(event) => {
          const value = Number(event.target.value)
          // A pointer drag previews; a key press (no drag in progress) seeks at once.
          if (scrub !== null) setScrub(value)
          else onSeek(value)
        }}
        onPointerDown={(event) => {
          event.currentTarget.setPointerCapture(event.pointerId)
          setScrub(position)
        }}
        onPointerUp={commit}
        onPointerCancel={() => setScrub(null)}
        onPointerMove={onPointerMove}
        onPointerLeave={() => setHover(null)}
        className="absolute inset-0 h-full w-full cursor-pointer appearance-none bg-transparent opacity-0 focus-visible:opacity-0 disabled:cursor-default"
      />
      <span className="pointer-events-none absolute -inset-y-1 inset-x-0 rounded-full ring-accent group-has-[:focus-visible]/seek:ring-2" />
    </div>
  )
}
