import type { ReactElement } from 'react'
import type { Tone } from '@/ui/Badge'
import { cn } from '@/lib/cn'
import { mediaStateTone, type MediaState } from '@/lib/status'

const TONE_TEXT: Record<Tone, string> = {
  neutral: 'text-muted',
  accent: 'text-accent',
  success: 'text-success',
  warning: 'text-warning',
  danger: 'text-danger',
  info: 'text-info',
}

/**
 * One glyph per state, distinguishable by shape alone: a filled disc is here, a half disc is partly
 * here, a ring is absent, a dashed ring is not out yet, an arrow is arriving, a cross has failed.
 * Drawn on a 12px grid so it sits on a text baseline at any size.
 */
export function StatusGlyph({ state, className }: { state: MediaState; className?: string }) {
  return (
    <svg
      viewBox="0 0 12 12"
      aria-hidden="true"
      className={cn('size-3 shrink-0', TONE_TEXT[mediaStateTone[state]], className)}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.4"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {GLYPHS[state]}
    </svg>
  )
}

const GLYPHS: Record<MediaState, ReactElement> = {
  available: <circle cx="6" cy="6" r="4.5" fill="currentColor" stroke="none" />,
  partial: (
    <>
      <circle cx="6" cy="6" r="4.3" />
      <path d="M6 1.7a4.3 4.3 0 0 1 0 8.6z" fill="currentColor" stroke="none" />
    </>
  ),
  notInLibrary: <circle cx="6" cy="6" r="4.3" />,
  missing: <circle cx="6" cy="6" r="4.3" />,
  upcoming: <circle cx="6" cy="6" r="4.3" strokeDasharray="2 1.6" />,
  queued: (
    <>
      <circle cx="6" cy="6" r="4.3" />
      <circle cx="6" cy="6" r="1.2" fill="currentColor" stroke="none" />
    </>
  ),
  downloading: <path d="M6 1.5v7M3 5.8 6 8.8l3-3M2.5 10.5h7" />,
  failed: <path d="M2.5 2.5l7 7M9.5 2.5l-7 7" />,
  watched: <path d="M2 6.3 4.8 9 10 3" />,
  inProgress: (
    <>
      <circle cx="6" cy="6" r="4.3" />
      <path d="M6 3.5V6l1.8 1.2" />
    </>
  ),
  monitored: (
    <>
      <path d="M1 6s1.8-3.5 5-3.5S11 6 11 6 9.2 9.5 6 9.5 1 6 1 6z" />
      <circle cx="6" cy="6" r="1.4" fill="currentColor" stroke="none" />
    </>
  ),
}

/**
 * A state in words with its glyph. The words are the status; the glyph and tone only reinforce it, so
 * this reads the same to a screen reader, in grayscale and to someone who cannot tell green from red.
 */
export function MediaStatus({
  state,
  label,
  className,
}: {
  state: MediaState
  label: string
  className?: string
}) {
  return (
    <span className={cn('inline-flex items-center gap-1.5 text-meta text-muted', className)}>
      <StatusGlyph state={state} />
      <span>{label}</span>
    </span>
  )
}
