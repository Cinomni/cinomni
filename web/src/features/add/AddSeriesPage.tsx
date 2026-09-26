import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router'
import { catalogApi, metadataApi, monitoringApi, requestsApi } from '@/api/endpoints'
import type { MetadataCandidate } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { PageHeader } from '@/components/PageHeader'
import { ApiError } from '@/lib/api'
import { externalIdsOf } from '@/lib/providers'
import { EmptyState } from '@/ui/EmptyState'
import { TvIcon } from '@/ui/icons'
import { Spinner } from '@/ui/Spinner'
import { CandidateRow, invalidateCatalogued, keyOf } from './CandidateRow'
import { SearchFailure } from './SearchFailure'
import { SearchForm } from './SearchForm'
import { useMetadataSearch } from './useMetadataSearch'

/**
 * Search the metadata providers for a show and put it in the library. Series results are merged
 * across providers, so one row is one show even when three providers know it.
 */
export function AddSeriesPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  // Only an administrator puts a title straight in the library; everyone else files a request.
  const canAddDirectly = user?.isAdministrator ?? false
  const autoApproved = user?.permissions?.requestsAutoApproved ?? false

  const [searchParams] = useSearchParams()
  const search = useMetadataSearch('Series', searchParams.get('q') ?? '')
  // Keyed per candidate: several rows can be in flight, and one settling must not clear another's state.
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set())
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({})
  const [requested, setRequested] = useState<ReadonlySet<string>>(new Set())

  function settle(key: string) {
    setBusy((current) => {
      const next = new Set(current)
      next.delete(key)
      return next
    })
  }

  function fail(key: string, error: unknown, fallback: string) {
    setErrors((current) => ({ ...current, [key]: error instanceof ApiError ? error.message : fallback }))
  }

  const add = useMutation({
    mutationFn: async (candidate: MetadataCandidate) => {
      // Carry every identity the merge found, or a later refresh from another provider would create
      // a second work for the same show.
      const externalIds = externalIdsOf(candidate)
      const { workId } = await catalogApi.addSeries({ title: candidate.title, year: candidate.year, externalIds })

      // Add, monitor and enrich: monitoring gives the acquisition spine something to chase, and the
      // refresh is what pulls the season/episode structure. Both are best-effort.
      await Promise.allSettled([
        monitoringApi.applyPolicy(workId, 'All'),
        metadataApi.refresh({
          workId,
          provider: candidate.provider,
          externalId: candidate.externalId,
          kind: 'Series',
        }),
      ])
      return workId
    },
    onSuccess: (workId) => {
      // Not awaited: the page is being left, and waiting for every row's lookup to refetch would keep
      // the button spinning for nothing.
      void invalidateCatalogued(queryClient)
      navigate(`/series/${workId}`)
    },
    onError: (error, candidate) => fail(keyOf(candidate), error, 'Could not add the show.'),
    onSettled: (_workId, _error, candidate) => settle(keyOf(candidate)),
  })

  const request = useMutation({
    mutationFn: (candidate: MetadataCandidate) =>
      requestsApi.submit({
        title: candidate.title,
        year: candidate.year,
        provider: candidate.provider,
        externalId: candidate.externalId,
        kind: 'Series',
      }),
    onSuccess: async (_result, candidate) => {
      setRequested((current) => new Set(current).add(keyOf(candidate)))
      await queryClient.invalidateQueries({ queryKey: ['requests'] })
      // Auto-approved, the show is catalogued by the time this resolves: the library and the row's
      // "already in the library" answer are stale, the same as for a movie.
      if (autoApproved) await invalidateCatalogued(queryClient)
    },
    onError: (error, candidate) => fail(keyOf(candidate), error, 'Could not submit the request.'),
    onSettled: (_result, _error, candidate) => settle(keyOf(candidate)),
  })

  function onSearch(event: FormEvent) {
    setErrors({})
    search.submit(event)
  }

  function onPick(candidate: MetadataCandidate) {
    const key = keyOf(candidate)
    setErrors((current) => {
      const { [key]: _removed, ...rest } = current
      return rest
    })
    setBusy((current) => new Set(current).add(key))
    if (canAddDirectly) {
      add.mutate(candidate)
    } else {
      request.mutate(candidate)
    }
  }

  return (
    <>
      <PageHeader
        title={canAddDirectly ? 'Add series' : 'Request a series'}
        subtitle={
          canAddDirectly
            ? 'Search your metadata providers, then add a show — its seasons and episodes follow.'
            : 'Search your metadata providers, then ask an administrator for a show.'
        }
      />

      <SearchForm
        term={search.term}
        onTermChange={search.setTerm}
        year={search.year}
        onYearChange={search.setYear}
        onSubmit={onSearch}
        isSearching={search.isPending}
      />

      {search.isPending ? (
        <div className="grid place-items-center py-20">
          <Spinner className="size-7 text-accent" />
        </div>
      ) : search.isError ? (
        <SearchFailure error={search.error} isAdministrator={canAddDirectly} />
      ) : search.candidates ? (
        search.candidates.length === 0 ? (
          <EmptyState title="No results" description={`No shows matched “${search.term}”.`} />
        ) : (
          <ul className="space-y-2">
            {search.candidates.map((candidate) => (
              <CandidateRow
                key={keyOf(candidate)}
                candidate={candidate}
                action={canAddDirectly ? 'Add' : 'Request'}
                busy={busy.has(keyOf(candidate))}
                done={requested.has(keyOf(candidate))}
                error={errors[keyOf(candidate)]}
                onPick={() => onPick(candidate)}
              />
            ))}
          </ul>
        )
      ) : (
        <EmptyState
          icon={<TvIcon className="size-9" />}
          title={canAddDirectly ? 'Search for a show' : 'Search for a show to request'}
          description="Results come from the metadata providers you have configured (TheTVDB, TVmaze, TMDB)."
        />
      )}
    </>
  )
}
