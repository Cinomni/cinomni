import { useMemo, type ReactNode } from 'react'
import type { Work } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { MediaPoster } from '@/components/media/MediaPoster'
import { MediaRail } from '@/components/media/MediaRail'
import { errorMessage } from '@/lib/api'
import { ApertureMark } from '@/ui/Brand'
import { ButtonLink } from '@/ui/Button'
import { ErrorState } from '@/ui/ErrorState'
import { FilmIcon, TvIcon } from '@/ui/icons'
import { SkeletonHero, SkeletonRail } from '@/ui/Skeleton'
import { HomeHero } from './HomeHero'
import { ContinueTile, UpcomingStub } from './HomeTiles'
import { newestFirst, pickFeatured, useHomeData } from './useHomeData'

/** Enough to fill two screen widths of a rail; the library page is where the rest lives. */
const RAIL_LIMIT = 20
const UPCOMING_LIMIT = 12

/**
 * Where the app opens: what to watch next, what just arrived, what is coming. Each row is its own
 * query, so one slow or failed source leaves the others standing; only the catalog itself — without
 * which there is nothing to show — takes over the page when it fails.
 */
export function HomePage() {
  const { user } = useAuth()
  const isAdmin = user?.isAdministrator ?? false
  const { works, assets, progress, calendar, importList, continueItems } = useHomeData(isAdmin)

  const workById = useMemo(() => new Map((works.data ?? []).map((work) => [work.id, work])), [works.data])
  const featured = useMemo(() => pickFeatured(works.data ?? []), [works.data])
  const recent = useMemo(() => newestFirst(works.data ?? []).slice(0, RAIL_LIMIT), [works.data])
  const trending = useMemo(
    () =>
      (importList.data?.entries ?? [])
        .map((entry) => (entry.workId ? workById.get(entry.workId) : undefined))
        .filter((work): work is Work => work !== undefined)
        .slice(0, RAIL_LIMIT),
    [importList.data, workById],
  )
  const upcoming = (calendar.data ?? []).slice(0, UPCOMING_LIMIT)
  const today = new Date()

  if (works.isPending) {
    return (
      <div className="space-y-10 pb-12">
        <SkeletonHero label="Loading your home page" />
        <SkeletonRail label="Loading recently added" />
      </div>
    )
  }

  if (works.isError) {
    return (
      <div className="gutter py-16">
        <ErrorState
          title="Your library couldn't be loaded"
          message={errorMessage(works.error)}
          onRetry={() => void works.refetch()}
        />
      </div>
    )
  }

  if (!featured) {
    return <WelcomeState isAdmin={isAdmin} canRequest={user?.permissions?.canRequest ?? false} />
  }

  return (
    <div className="pb-12">
      <h1 className="sr-only">Home</h1>
      <HomeHero work={featured} assets={assets.data ?? []} continueItems={continueItems} />

      <div className="-mt-4 space-y-12">
        {progress.isError ? (
          <RailNotice title="Continue watching" onRetry={() => void progress.refetch()}>
            {errorMessage(progress.error, 'Your viewing progress could not be read.')}
          </RailNotice>
        ) : (
          continueItems.length > 0 && (
            <MediaRail
              title="Continue watching"
              items={continueItems}
              itemKey={(item) => item.assetId}
              shape="landscape"
              renderItem={(item) => <ContinueTile item={item} />}
            />
          )
        )}

        <MediaRail
          title="Recently added"
          items={recent}
          itemKey={(work) => work.id}
          shape="posterLarge"
          seeAllHref="/library?sort=added"
          renderItem={(work) => <MediaPoster work={work} />}
        />

        {calendar.isError ? (
          <RailNotice title="Upcoming" onRetry={() => void calendar.refetch()}>
            {errorMessage(calendar.error, 'The airing schedule could not be read.')}
          </RailNotice>
        ) : (
          upcoming.length > 0 && (
            <MediaRail
              title="Upcoming"
              description="Monitored episodes airing in the next two weeks"
              items={upcoming}
              itemKey={(entry) => entry.id}
              shape="landscape"
              seeAllHref="/calendar"
              renderItem={(entry) => <UpcomingStub entry={entry} work={workById.get(entry.workId)} today={today} />}
            />
          )
        )}

        {trending.length > 0 && (
          <MediaRail
            title="Trending"
            description="From your import lists, already in the library"
            items={trending}
            itemKey={(work) => work.id}
            seeAllHref="/console/import-list"
            renderItem={(work) => <MediaPoster work={work} />}
          />
        )}
      </div>
    </div>
  )
}

/** A rail that could not load: its heading stays, so the page does not silently lose a row. */
function RailNotice({ title, onRetry, children }: { title: string; onRetry: () => void; children: ReactNode }) {
  return (
    <section className="gutter">
      <h2 className="text-section text-fg">{title}</h2>
      <p role="alert" className="mt-2 flex flex-wrap items-center gap-3 text-meta text-muted">
        <span>{children}</span>
        <button type="button" onClick={onRetry} className="text-accent underline-offset-4 hover:underline">
          Retry
        </button>
      </p>
    </section>
  )
}

/** The first run: an empty library explains what the app does and offers the first step. */
function WelcomeState({ isAdmin, canRequest }: { isAdmin: boolean; canRequest: boolean }) {
  const verb = isAdmin ? 'Add' : 'Request'
  return (
    <section className="gutter flex min-h-[70vh] flex-col items-start justify-center py-16">
      <ApertureMark className="size-12 text-accent" />
      <h1 className="mt-6 max-w-xl text-title text-fg sm:text-display">Your library is empty</h1>
      <p className="mt-4 max-w-lg text-body text-muted">
        {isAdmin
          ? 'Add a movie or a series and Cinomni will find it, download it, import it and have it ready to play — with subtitles.'
          : canRequest
            ? 'Request a movie or a series. Once an administrator approves it, it appears here, ready to play.'
            : 'Nothing has been added yet. Titles appear here as soon as an administrator adds them.'}
      </p>
      {(isAdmin || canRequest) && (
        <div className="mt-8 flex flex-wrap gap-2">
          <ButtonLink to="/add" size="lg" icon={<FilmIcon className="size-5" />}>
            {verb} a movie
          </ButtonLink>
          <ButtonLink to="/add/series" size="lg" variant="subtle" icon={<TvIcon className="size-5" />}>
            {verb} a series
          </ButtonLink>
        </div>
      )}
    </section>
  )
}
