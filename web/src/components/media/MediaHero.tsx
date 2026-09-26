import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { cn } from '@/lib/cn'
import { ChevronLeftIcon } from '@/ui/icons'
import { PosterArt } from './MediaPoster'
import { backdropSources, type HeroArtwork } from './artwork'

/**
 * The full-bleed title hero shared by Home and the detail pages. The backdrop runs edge to edge; two
 * scrims (bottom → page, left → text) exist only so the text on top of it stays legible over any
 * artwork. Content sits bottom-left, like a title card: eyebrow metadata, the title, an optional
 * synopsis, the state line and one row of actions whose first entry is the contextual primary action.
 */
export function MediaHero({
  work,
  eyebrow,
  overview,
  status,
  actions,
  children,
  showPoster = false,
  headingLevel = 'h1',
  size = 'detail',
  back,
}: {
  work: HeroArtwork
  eyebrow?: ReactNode
  overview?: string | null
  status?: ReactNode
  actions?: ReactNode
  /** Below the actions: mutation feedback that belongs to them. */
  children?: ReactNode
  /** The poster beside the text, for a detail page on a wide screen. */
  showPoster?: boolean
  headingLevel?: 'h1' | 'h2'
  size?: 'home' | 'detail'
  /** Where "back" goes, drawn over the top of the artwork. */
  back?: { to: string; label: string }
}) {
  const Heading = headingLevel
  const backdrop = backdropSources(work)
  // Blurred-looking at 25% opacity and scaled up anyway: the small rendition is plenty.
  const dimmedPoster = work.posterSmallUrl ?? work.posterUrl
  return (
    <section className="relative isolate">
      <div aria-hidden="true" className="absolute inset-0 -z-10 overflow-hidden bg-surface">
        {backdrop ? (
          <img
            src={backdrop.src}
            srcSet={backdrop.srcSet}
            sizes={backdrop.srcSet ? '100vw' : undefined}
            alt=""
            decoding="async"
            className="size-full object-cover object-[center_20%]"
          />
        ) : (
          // No backdrop: the poster, heavily scaled and dimmed, still gives the page the title's colors.
          dimmedPoster && <img src={dimmedPoster} alt="" className="size-full scale-110 object-cover opacity-25" />
        )}
        {/* Solid for the last few percent, so the artwork never ends in a visible edge. */}
        <div className="absolute inset-0 bg-linear-to-t from-bg from-8% via-bg/55 via-45% to-bg/5" />
        <div className="absolute inset-x-0 top-0 h-32 bg-linear-to-b from-bg/60 to-transparent" />
        <div className="absolute inset-0 bg-linear-to-r from-bg/85 via-bg/35 to-transparent" />
      </div>

      {back && (
        <div className="gutter absolute inset-x-0 top-4 md:top-6">
          <Link
            to={back.to}
            className="inline-flex items-center gap-1 rounded-control py-1 pr-2 text-meta text-fg/80 transition-colors hover:text-fg"
          >
            <ChevronLeftIcon className="size-4" /> {back.label}
          </Link>
        </div>
      )}

      <div
        className={cn(
          'gutter flex items-end gap-8 pb-8 pt-28 sm:pb-10',
          size === 'home' ? 'min-h-[min(78vh,44rem)]' : 'min-h-[min(70vh,38rem)]',
        )}
      >
        {showPoster && (
          <div className="hidden w-48 shrink-0 overflow-hidden rounded-media shadow-[var(--shadow-lift)] ring-1 ring-inset ring-white/8 lg:block xl:w-56">
            <div className="aspect-2/3">
              <PosterArt work={work} />
            </div>
          </div>
        )}

        <div className="min-w-0 max-w-2xl">
          {eyebrow}
          <Heading className="mt-3 text-balance text-title text-fg sm:text-display">
            {work.title}
          </Heading>
          {overview && <p className="mt-4 line-clamp-3 max-w-xl text-body text-fg/85 sm:line-clamp-4">{overview}</p>}
          {status && <div className="mt-4">{status}</div>}
          {actions && <div className="mt-6 flex flex-wrap items-center gap-2">{actions}</div>}
          {children}
        </div>
      </div>
    </section>
  )
}
