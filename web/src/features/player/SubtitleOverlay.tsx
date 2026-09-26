import { cn } from '@/lib/cn'
import type { SubtitleSize } from './PlayerSettings'
import { useSubtitleCues } from './useSubtitleCues'
import { activeText } from './vtt'

const SIZE: Record<SubtitleSize, string> = {
  small: 'text-[clamp(0.875rem,2.1vw,1.375rem)]',
  medium: 'text-[clamp(1rem,2.8vw,1.875rem)]',
  large: 'text-[clamp(1.25rem,3.6vw,2.5rem)]',
}

/**
 * The subtitles, drawn by the app over the picture rather than by the browser's own track renderer: a
 * stream that starts part way into the file has its own zero, and only here is the file's time known.
 * Cue text is rendered as text, never as markup. Sits higher while the controls are showing.
 */
export function SubtitleOverlay({
  url,
  time,
  size,
  raised,
}: {
  url: string | null
  /** Seconds into the file. */
  time: number
  size: SubtitleSize
  raised: boolean
}) {
  const { cues, isError } = useSubtitleCues(url)
  const text = url ? activeText(cues, time) : ''

  return (
    <div
      className={cn(
        'pointer-events-none absolute inset-x-0 flex justify-center px-6 transition-[bottom] duration-200 motion-reduce:transition-none',
        raised ? 'bottom-28 sm:bottom-32' : 'bottom-6 sm:bottom-10',
      )}
      aria-live="off"
    >
      {text && (
        <p
          className={cn(
            'max-w-[min(90%,60rem)] whitespace-pre-line text-center font-medium leading-snug text-white',
            SIZE[size],
          )}
        >
          <span className="rounded-md bg-black/70 box-decoration-clone px-2 py-0.5 [text-shadow:0_1px_2px_rgb(0_0_0/0.8)]">
            {text}
          </span>
        </p>
      )}
      {isError && (
        <p className="rounded-md bg-black/70 px-2 py-1 text-xs text-muted">These subtitles could not be loaded.</p>
      )}
    </div>
  )
}
