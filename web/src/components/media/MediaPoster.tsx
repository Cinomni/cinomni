import { Link } from 'react-router'
import type { Work } from '@/api/types'
import { cn } from '@/lib/cn'
import { workPath } from '@/lib/routes'
import { availabilityLabel, availabilityOf, mediaStateFromAvailability } from '@/lib/status'
import { FilmIcon, TvIcon } from '@/ui/icons'
import { StatusGlyph } from './MediaStatus'

/** How far along a title the viewer is: a 0..1 fraction drawn as the marquee line on the bottom edge. */
export function ProgressEdge({ value, label }: { value: number; label: string }) {
  const clamped = Math.min(1, Math.max(0, value))
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(clamped * 100)}
      className="absolute inset-x-0 bottom-0 h-0.75 bg-black/50"
    >
      <div className="h-full bg-accent" style={{ width: `${clamped * 100}%` }} />
    </div>
  )
}

/**
 * Artwork or, without it, the title set on the surface — a missing poster should still say what it is.
 * Every poster on screen is a tile or a sidebar at most a couple of hundred pixels wide, so the small
 * rendition is what loads; the original only when the server has no smaller one to offer.
 */
export function PosterArt({
  work,
  className,
}: {
  work: Pick<Work, 'title' | 'kind' | 'posterUrl' | 'posterSmallUrl'>
  className?: string
}) {
  const src = work.posterSmallUrl ?? work.posterUrl
  if (src) {
    return (
      <img
        src={src}
        alt=""
        loading="lazy"
        decoding="async"
        className={cn('size-full object-cover', className)}
      />
    )
  }
  const Glyph = work.kind === 'Series' ? TvIcon : FilmIcon
  return (
    // Hidden from assistive technology: the tile around it already names the title.
    <div aria-hidden="true" className={cn('flex size-full flex-col justify-between bg-elevated p-3', className)}>
      <Glyph className="size-5 text-faint" />
      {/* Generated content, not text: the tile around it already names the title once. */}
      <span
        data-title={work.title}
        className="line-clamp-4 text-card font-semibold text-muted before:content-[attr(data-title)]"
      />
    </div>
  )
}

/**
 * The poster tile — the unit of every media surface. Borderless 2:3 artwork with a hairline inner edge
 * (the "print edge"), title and year set below it rather than boxed with it. State is quiet by default:
 * a title that is fully here shows nothing extra; one that is partly here or absent carries a glyph in
 * the corner, and the full sentence is always available to assistive technology and on hover.
 */
export function MediaPoster({
  work,
  progress,
  subtitle,
  className,
}: {
  work: Work
  /** 0..1 watched fraction, when known. */
  progress?: number
  /** Replaces the year line, e.g. "Trending · added today". */
  subtitle?: string
  className?: string
}) {
  const isSeries = work.kind === 'Series'
  const availability = availabilityOf(work)
  const statusText =
    isSeries && work.episodeCount > 0
      ? `${availabilityLabel[availability]} — ${work.availableEpisodeCount} of ${work.episodeCount} episodes`
      : availabilityLabel[availability]

  return (
    <Link
      to={workPath(work)}
      className={cn('group block rounded-media outline-none', className)}
    >
      <div
        className={cn(
          'relative aspect-2/3 overflow-hidden rounded-media bg-surface',
          'ring-2 ring-transparent ring-offset-2 ring-offset-bg transition-shadow duration-150',
          'group-focus-visible:ring-accent',
        )}
      >
        <PosterArt
          work={work}
          className="transition-transform duration-200 ease-out-quint group-hover:scale-[1.04] motion-reduce:group-hover:scale-100"
        />
        {/* The print edge: a hairline highlight inside the artwork, not a border around it. */}
        <span aria-hidden="true" className="pointer-events-none absolute inset-0 rounded-media ring-1 ring-inset ring-white/8" />

        {availability !== 'complete' && (
          <span
            aria-hidden="true"
            className="absolute left-2 top-2 grid size-6 place-items-center rounded-full bg-scrim/80"
          >
            <StatusGlyph state={mediaStateFromAvailability[availability]} className="size-3.5" />
          </span>
        )}

        {/* The state in words: revealed on hover and focus, always read by assistive technology. */}
        <span
          className={cn(
            'absolute inset-x-0 bottom-0 bg-linear-to-t from-scrim/95 via-scrim/60 to-transparent px-2.5 pb-2.5 pt-8',
            'text-meta text-fg opacity-0 transition-opacity duration-200 group-hover:opacity-100 group-focus-visible:opacity-100',
          )}
        >
          {statusText}
        </span>

        {progress != null && progress > 0 && <ProgressEdge value={progress} label={`${work.title} progress`} />}
      </div>

      <div className="mt-2 min-w-0 px-0.5">
        <p className="truncate text-card text-fg transition-colors group-hover:text-accent-strong">{work.title}</p>
        <p className="truncate text-meta tabular-nums text-faint">{subtitle ?? work.year ?? '—'}</p>
      </div>
    </Link>
  )
}
