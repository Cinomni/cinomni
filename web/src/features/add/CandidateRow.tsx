import { useQuery, type QueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { catalogApi } from '@/api/endpoints'
import type { MetadataCandidate } from '@/api/types'
import { externalIdsOf } from '@/lib/providers'
import { workPath } from '@/lib/routes'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'

/** Stable per-row key: a candidate is identified by the provider it came from plus that provider's id. */
export const keyOf = (candidate: MetadataCandidate) => `${candidate.provider}-${candidate.externalId}`

/**
 * Whether this candidate is already in the catalog, checked once per row against the identity the
 * user actually saw. A miss (404) and a genuine failure both read as "unknown" here — only a
 * confirmed hit changes what the row offers — so a down lookup degrades to today's Add/Request
 * behaviour instead of blocking the row or claiming the title is new.
 */
const WORK_BY_EXTERNAL_KEY = ['catalog', 'work-by-external'] as const

/**
 * What a title entering the catalog makes stale: the library lists, and every row's "is it already
 * here?" answer — cached for minutes, it would otherwise go on offering Add for the title just added.
 */
export function invalidateCatalogued(queryClient: QueryClient): Promise<void> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['works'] }),
    queryClient.invalidateQueries({ queryKey: WORK_BY_EXTERNAL_KEY }),
  ]).then(() => undefined)
}

function useExistingWork(candidate: MetadataCandidate) {
  const identity = externalIdsOf(candidate)[0] ?? null

  return useQuery({
    queryKey: [...WORK_BY_EXTERNAL_KEY, identity?.provider ?? '', identity?.value ?? '', candidate.kind],
    queryFn: ({ signal }) =>
      identity
        ? catalogApi.getByExternalId(identity.provider, identity.value, signal, candidate.kind)
        : Promise.resolve(null),
    enabled: identity !== null,
    retry: false,
    staleTime: 5 * 60_000,
  })
}

/**
 * One provider search result, with the single action the caller is allowed to take on it — an
 * administrator adds the title outright, everyone else files a request. Shared by the movie and the
 * series search so the two pages cannot drift apart visually.
 */
export function CandidateRow({
  candidate,
  action,
  allowed = true,
  busy,
  done,
  error,
  onPick,
}: {
  candidate: MetadataCandidate
  action: 'Add' | 'Request'
  /** False when the account may not request: the API refuses it, so the UI does not offer it. */
  allowed?: boolean
  busy: boolean
  done: boolean
  error?: string
  onPick: () => void
}) {
  const existing = useExistingWork(candidate)
  const existingWork = existing.isSuccess ? existing.data : null

  // A series candidate is merged across providers, so show the identities it was merged on.
  const crossReferences = [candidate.tvdbId && 'TheTVDB', candidate.imdbId && 'IMDb', candidate.tmdbId && 'TMDB']
    .filter((name): name is string => !!name)

  return (
    <Card as="li" className="flex items-start gap-4">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <p className="font-medium text-fg">{candidate.title}</p>
          <span className="text-sm text-faint">{candidate.year ?? '—'}</span>
          <Badge tone="accent">{candidate.provider}</Badge>
        </div>
        {candidate.overview && <p className="mt-1 line-clamp-2 text-sm text-muted">{candidate.overview}</p>}
        {crossReferences.length > 1 && (
          <p className="mt-1 text-xs text-faint">Matched on {crossReferences.join(', ')}</p>
        )}
        {error && (
          <p role="alert" className="mt-2 text-sm text-danger">
            {error}
          </p>
        )}
      </div>
      {existingWork ? (
        <div className="flex flex-col items-end gap-1.5">
          <Badge tone="success">In library</Badge>
          <Link to={workPath(existingWork)} className="text-sm font-medium text-accent hover:underline">
            View
          </Link>
        </div>
      ) : done ? (
        <Badge tone="success">Requested</Badge>
      ) : (
        <Button size="sm" variant="subtle" loading={busy} disabled={!allowed} onClick={onPick}>
          {action}
        </Button>
      )}
    </Card>
  )
}
