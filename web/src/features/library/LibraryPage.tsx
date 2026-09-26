import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { catalogApi } from '@/api/endpoints'
import type { WorkAvailabilityFilter, WorkKind, WorkPageQuery, WorkSortKey } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { MediaPoster } from '@/components/media/MediaPoster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import type { Availability } from '@/lib/status'
import { Button, ButtonLink } from '@/ui/Button'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { FilmIcon, FilterIcon, GridCompactIcon, GridLargeIcon, PlusIcon, SearchIcon, TvIcon } from '@/ui/icons'
import { Segmented, type SegmentedOption } from '@/ui/Segmented'
import { SkeletonPoster } from '@/ui/Skeleton'
import { SEARCH_DEBOUNCE_MS, useDebouncedValue, useLibraryFacets, useLibraryPages } from './useLibraryPages'

type KindFilter = 'All' | WorkKind
const KIND_FILTER_VALUES: readonly string[] = ['All', 'Movie', 'Series']
function isKindFilter(value: string): value is KindFilter {
  return KIND_FILTER_VALUES.includes(value)
}

type SortKey = 'title' | 'year' | 'added'
const SORT_VALUES: readonly string[] = ['title', 'year', 'added']
function isSortKey(value: string | null): value is SortKey {
  return value != null && SORT_VALUES.includes(value)
}

type AvailabilityFilter = 'all' | Availability
const AVAILABILITY_VALUES: readonly string[] = ['all', 'complete', 'partial', 'none']
function isAvailabilityFilter(value: string): value is AvailabilityFilter {
  return AVAILABILITY_VALUES.includes(value)
}

/** The page's own filter vocabulary, as the server names it. */
const SORT_ON_WIRE: Record<SortKey, WorkSortKey> = { title: 'Title', year: 'Year', added: 'Added' }
const AVAILABILITY_ON_WIRE: Record<Availability, WorkAvailabilityFilter> = {
  complete: 'Complete',
  partial: 'Partial',
  none: 'None',
}

type Density = 'large' | 'compact'
const DENSITY_STORAGE_KEY = 'cinomni.library.density'

/** The density is a per-browser convenience: storage can be missing or refuse, and the page must not care. */
function readDensity(): Density {
  try {
    return localStorage.getItem(DENSITY_STORAGE_KEY) === 'compact' ? 'compact' : 'large'
  } catch {
    return 'large'
  }
}

function writeDensity(density: Density): void {
  try {
    localStorage.setItem(DENSITY_STORAGE_KEY, density)
  } catch {
    // A private window or blocked storage only costs remembering the choice.
  }
}

const GRID: Record<Density, string> = {
  large: 'grid-cols-2 gap-x-4 gap-y-7 sm:grid-cols-[repeat(auto-fill,minmax(10.5rem,1fr))] lg:gap-x-5',
  compact: 'grid-cols-3 gap-x-3 gap-y-5 sm:grid-cols-[repeat(auto-fill,minmax(7.5rem,1fr))]',
}

const PAGE_COPY: Record<KindFilter, { title: string; noun: string; plural: string }> = {
  All: { title: 'Library', noun: 'title', plural: 'titles' },
  Movie: { title: 'Movies', noun: 'movie', plural: 'movies' },
  Series: { title: 'Series', noun: 'series', plural: 'series' },
}

/**
 * The library, as this account may see it — all of it, or one kind when reached as Movies or Series.
 * The collection, kind, availability, genre and text filters are a convenience, not a boundary: the API
 * already answers only with what the caller may browse. Filtering and ordering run on the server, and
 * the grid loads a page at a time as the viewer reaches its end, so a large library costs what is on
 * screen rather than all of it.
 */
export function LibraryPage({ kind: lockedKind }: { kind?: WorkKind }) {
  const { user } = useAuth()
  const [searchParams] = useSearchParams()
  const filtersId = useId()
  const [collectionId, setCollectionId] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [kindChoice, setKindChoice] = useState<KindFilter>('All')
  const [availability, setAvailability] = useState<AvailabilityFilter>('all')
  const [genre, setGenre] = useState<string | null>(null)
  const [sort, setSort] = useState<SortKey>(() => {
    const requested = searchParams.get('sort')
    return isSortKey(requested) ? requested : 'title'
  })
  const [density, setDensity] = useState<Density>(readDensity)
  const [filtersOpen, setFiltersOpen] = useState(false)

  const kind: KindFilter = lockedKind ?? kindChoice
  const copy = PAGE_COPY[lockedKind ?? 'All']
  const term = useDebouncedValue(filter.trim(), SEARCH_DEBOUNCE_MS)

  const query: WorkPageQuery = {
    collectionId: collectionId ?? undefined,
    kind: kind === 'All' ? undefined : kind,
    q: term || undefined,
    availability: availability === 'all' ? undefined : AVAILABILITY_ON_WIRE[availability],
    genre: genre ?? undefined,
    sort: SORT_ON_WIRE[sort],
  }

  const { data: collections } = useQuery({ queryKey: ['collections'], queryFn: catalogApi.collections })
  const facets = useLibraryFacets(collectionId, lockedKind)
  const { pages, works, total } = useLibraryPages(query)

  const movies = facets.data?.movies ?? 0
  const series = facets.data?.series ?? 0
  // How many titles this page is about before any filter but the shelf: what "empty" is measured by.
  const shelfCount = lockedKind === 'Movie' ? movies : lockedKind === 'Series' ? series : movies + series
  const hasTitles = facets.data ? shelfCount > 0 : works.length > 0
  const kindOptions: SegmentedOption[] = [
    { value: 'All', label: 'All', count: movies + series },
    { value: 'Movie', label: 'Movies', count: movies },
    { value: 'Series', label: 'Series', count: series },
  ]
  const genres = facets.data?.genres ?? []

  // A single shelf is the default install: a picker for it would be noise.
  const shelves = collections && collections.length > 1 ? collections : []
  const activeFilterCount = (availability !== 'all' ? 1 : 0) + (collectionId ? 1 : 0) + (genre ? 1 : 0)
  // Only an administrator puts a title straight in the library; everyone else files a request.
  const canRequest = user?.isAdministrator || (user?.permissions?.canRequest ?? false)
  const addVerb = user?.isAdministrator ? 'Add' : 'Request'

  const addAction = canRequest ? (
    lockedKind === 'Series' ? (
      <ButtonLink to="/add/series" variant="subtle" icon={<PlusIcon className="size-4" />}>
        {addVerb} series
      </ButtonLink>
    ) : lockedKind === 'Movie' ? (
      <ButtonLink to="/add" variant="subtle" icon={<PlusIcon className="size-4" />}>
        {addVerb} movie
      </ButtonLink>
    ) : (
      <div className="flex gap-2">
        <ButtonLink to="/add" variant="subtle" icon={<FilmIcon className="size-4" />}>
          {addVerb} movie
        </ButtonLink>
        <ButtonLink to="/add/series" variant="subtle" icon={<TvIcon className="size-4" />}>
          {addVerb} series
        </ButtonLink>
      </div>
    )
  ) : null

  const emptyShelf = (
    <EmptyState
      icon={lockedKind === 'Series' ? <TvIcon className="size-10" /> : <FilmIcon className="size-10" />}
      title={collectionId ? 'Nothing on this shelf' : `No ${copy.plural} yet`}
      description={
        user?.isAdministrator
          ? `Add a ${copy.noun === 'title' ? 'movie or series' : copy.noun} and Cinomni will monitor it, download it and have it ready to watch.`
          : canRequest
            ? `Request a ${copy.noun === 'title' ? 'movie or series' : copy.noun} and it appears here once an administrator approves it.`
            : 'Titles appear here as soon as an administrator adds them.'
      }
      action={addAction}
    />
  )

  const noMatches = (
    <EmptyState
      title="No matches"
      description={
        term
          ? `Nothing ${kind === 'All' || lockedKind ? '' : `in ${kind === 'Movie' ? 'movies' : 'series'} `}matches “${term}”${availability !== 'all' || genre ? ' with these filters' : ''}.`
          : availability !== 'all' || genre
            ? 'Nothing here matches the filters.'
            : `Nothing in this library is a ${kind === 'Movie' ? 'movie' : 'series'} yet.`
      }
    />
  )

  return (
    <div className="gutter pb-16 pt-8 md:pt-10">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-title text-fg">{copy.title}</h1>
          {facets.data && (
            <p className="mt-1 text-meta tabular-nums text-faint">
              {shelfCount} {shelfCount === 1 ? copy.noun : copy.plural}
            </p>
          )}
        </div>
        {addAction}
      </header>

      {hasTitles && (
        <div className="mt-6 space-y-4">
          <div className="flex flex-wrap items-center gap-2">
            <label className="flex h-11 min-w-0 flex-1 basis-full items-center gap-3 rounded-control bg-surface px-3.5 ring-1 ring-inset ring-line-soft transition-shadow focus-within:ring-accent sm:basis-auto sm:max-w-md">
              <SearchIcon className="size-4 shrink-0 text-faint" />
              <input
                type="search"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder={`Search ${copy.plural}`}
                // A placeholder is not a label: it is gone the moment the field has a value, which is
                // exactly when someone re-reading the form needs to know what the field is.
                aria-label="Filter by title"
                className="h-full min-w-0 flex-1 bg-transparent text-body text-fg placeholder:text-faint focus:outline-none"
              />
            </label>

            {!lockedKind && (
              <Segmented
                label="Filter by kind"
                options={kindOptions}
                value={kind}
                onChange={(value) => {
                  if (isKindFilter(value)) setKindChoice(value)
                }}
              />
            )}

            <div className="ml-auto flex items-center gap-1">
              <label className="sr-only" htmlFor={`${filtersId}-sort`}>
                Sort by
              </label>
              <select
                id={`${filtersId}-sort`}
                value={sort}
                onChange={(event) => {
                  const { value } = event.target
                  if (isSortKey(value)) setSort(value)
                }}
                className="h-10 rounded-control bg-transparent px-2 text-card text-muted transition-colors hover:bg-hover hover:text-fg focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
              >
                <option value="title">Title (A–Z)</option>
                <option value="year">Year (newest first)</option>
                <option value="added">Recently added</option>
              </select>

              <div role="group" aria-label="Poster size" className="flex">
                <DensityButton
                  label="Large posters"
                  pressed={density === 'large'}
                  onClick={() => {
                    setDensity('large')
                    writeDensity('large')
                  }}
                >
                  <GridLargeIcon className="size-4.5" />
                </DensityButton>
                <DensityButton
                  label="Compact posters"
                  pressed={density === 'compact'}
                  onClick={() => {
                    setDensity('compact')
                    writeDensity('compact')
                  }}
                >
                  <GridCompactIcon className="size-4.5" />
                </DensityButton>
              </div>

              <button
                type="button"
                aria-expanded={filtersOpen}
                aria-controls={`${filtersId}-panel`}
                onClick={() => setFiltersOpen((open) => !open)}
                className={cn(
                  'inline-flex h-10 items-center gap-2 rounded-control px-3 text-card transition-colors hover:bg-hover',
                  filtersOpen || activeFilterCount > 0 ? 'text-fg' : 'text-muted',
                )}
              >
                <FilterIcon className="size-4.5" />
                Filters
                {activeFilterCount > 0 && (
                  <span className="grid size-5 place-items-center rounded-full bg-accent text-label text-on-accent">
                    {activeFilterCount}
                  </span>
                )}
              </button>
            </div>
          </div>

          <div id={`${filtersId}-panel`} hidden={!filtersOpen} className="space-y-4 border-y border-line-soft py-4">
            <FilterRow label="Availability">
              <Segmented
                label="Filter by availability"
                options={[
                  { value: 'all', label: 'Any' },
                  { value: 'complete', label: 'In library' },
                  { value: 'partial', label: 'Partly in library' },
                  { value: 'none', label: 'Not in library' },
                ]}
                value={availability}
                onChange={(value) => {
                  if (isAvailabilityFilter(value)) setAvailability(value)
                }}
              />
            </FilterRow>
            {genres.length > 0 && (
              <FilterRow label="Genre">
                <label className="sr-only" htmlFor={`${filtersId}-genre`}>
                  Filter by genre
                </label>
                <select
                  id={`${filtersId}-genre`}
                  value={genre ?? ''}
                  onChange={(event) => setGenre(event.target.value || null)}
                  className="h-10 min-w-40 rounded-control bg-surface px-3 text-card text-fg ring-1 ring-inset ring-line-soft focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <option value="">Any genre</option>
                  {genres.map(({ genre: name, count }) => (
                    <option key={name} value={name}>
                      {name} ({count})
                    </option>
                  ))}
                </select>
              </FilterRow>
            )}
            {shelves.length > 0 && (
              <FilterRow label="Collection">
                <Segmented
                  label="Filter by collection"
                  options={[
                    { value: '', label: 'All' },
                    ...shelves.map((collection) => ({
                      value: collection.id,
                      label: collection.name,
                      count: collection.workCount,
                    })),
                  ]}
                  value={collectionId ?? ''}
                  onChange={(value) => setCollectionId(value || null)}
                />
              </FilterRow>
            )}
          </div>
        </div>
      )}

      <div className="mt-8">
        {pages.isPending ? (
          <div role="status" aria-label={`Loading ${copy.plural}`} className={cn('grid', GRID[density])}>
            {Array.from({ length: 12 }, (_, index) => (
              <SkeletonPoster key={index} />
            ))}
          </div>
        ) : pages.isError && works.length === 0 ? (
          <ErrorState
            title={`Your ${copy.plural} couldn't be loaded`}
            message={errorMessage(pages.error)}
            onRetry={() => void pages.refetch()}
          />
        ) : !hasTitles ? (
          emptyShelf
        ) : total === 0 ? (
          noMatches
        ) : (
          <>
            <ul
              aria-busy={pages.isPlaceholderData}
              className={cn('grid transition-opacity', GRID[density], pages.isPlaceholderData && 'opacity-60')}
            >
              {works.map((work) => (
                <li key={work.id} className="defer-render">
                  <MediaPoster work={work} />
                </li>
              ))}
            </ul>
            <LoadMore
              shown={works.length}
              total={total}
              plural={copy.plural}
              hasMore={pages.hasNextPage}
              loading={pages.isFetchingNextPage}
              failed={pages.isFetchNextPageError}
              onLoad={() => void pages.fetchNextPage()}
            />
          </>
        )}
      </div>
    </div>
  )
}

/**
 * The end of the grid: reaching it loads the next page on its own, and the button does the same for
 * a keyboard, a screen reader, or a browser without IntersectionObserver. A page that failed says so
 * here and keeps what already loaded.
 */
function LoadMore({
  shown,
  total,
  plural,
  hasMore,
  loading,
  failed,
  onLoad,
}: {
  shown: number
  total: number
  plural: string
  hasMore: boolean
  loading: boolean
  failed: boolean
  onLoad: () => void
}) {
  const sentinel = useRef<HTMLDivElement>(null)
  // Read through a ref so a new callback identity does not re-create the observer on every render.
  const onLoadRef = useRef(onLoad)
  useEffect(() => {
    onLoadRef.current = onLoad
  }, [onLoad])
  const autoLoad = hasMore && !loading && !failed

  useEffect(() => {
    const node = sentinel.current
    if (!node || !autoLoad || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) onLoadRef.current()
      },
      // Start before the very end, so the next posters are there by the time they scroll in.
      { rootMargin: '800px 0px' },
    )
    observer.observe(node)
    return () => observer.disconnect()
  }, [autoLoad])

  return (
    <div ref={sentinel} className="mt-10 flex flex-col items-center gap-3">
      <p className="text-meta tabular-nums text-faint">
        {shown} of {total} {plural}
      </p>
      {failed && (
        <p role="alert" className="text-meta text-muted">
          The next {plural} could not be loaded.
        </p>
      )}
      {hasMore && (
        <Button variant="subtle" loading={loading} onClick={onLoad}>
          {failed ? 'Try again' : `Show more ${plural}`}
        </Button>
      )}
    </div>
  )
}

function FilterRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:gap-6">
      <p className="w-28 shrink-0 text-label uppercase text-faint">{label}</p>
      {children}
    </div>
  )
}

function DensityButton({
  label,
  pressed,
  onClick,
  children,
}: {
  label: string
  pressed: boolean
  onClick: () => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={pressed}
      onClick={onClick}
      className={cn(
        'grid size-10 place-items-center rounded-control transition-colors hover:bg-hover',
        pressed ? 'text-fg' : 'text-faint',
      )}
    >
      {children}
    </button>
  )
}
