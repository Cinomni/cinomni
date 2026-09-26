import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Navigate, useParams } from 'react-router'
import { catalogApi, metadataApi } from '@/api/endpoints'
import type { MonitoringMode, NextUpEpisode, Work } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { MediaHero } from '@/components/media/MediaHero'
import { primaryActionFor } from '@/components/media/primaryAction'
import { WorkAvailability, WorkEyebrow } from '@/components/media/WorkFacts'
import { ApiError, errorMessage } from '@/lib/api'
import { refreshSourceFor } from '@/lib/providers'
import { Alert } from '@/ui/Alert'
import { ButtonLink } from '@/ui/Button'
import { ImageIcon, PlayIcon, RefreshIcon, TrashIcon, TvIcon } from '@/ui/icons'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { Menu, type MenuItem } from '@/ui/Menu'
import { Skeleton, SkeletonHero } from '@/ui/Skeleton'
import { ArtworkPicker } from '@/features/work/ArtworkPicker'
import { RemoveWorkDialog } from '@/features/work/RemoveWorkDialog'
import { MonitoringModePicker } from './MonitoringModePicker'
import { SeasonAccordion } from './SeasonAccordion'
import {
  joinSeasonsWithTargets,
  seriesKeys,
  type FanOutWindow,
  useFanOutWindow,
  useNextUp,
  useSeriesAssets,
  useSeriesMonitoring,
  useSeriesSeasons,
} from './useSeries'

/**
 * The detail surface of a series: the show, its monitoring policy, and its seasons as collapsible
 * sections that load their episodes on demand. A movie that lands here is sent back to its own page.
 */
export function SeriesDetailPage() {
  const { id = '' } = useParams()
  const { user } = useAuth()
  // Adding, monitoring, refreshing and artwork are operator actions — the API refuses them to anyone
  // else, so a regular account is shown the state without a control that would only fail.
  const canManage = user?.isAdministrator ?? false
  const [pickingArtwork, setPickingArtwork] = useState(false)
  const [removing, setRemoving] = useState(false)

  // One window shared by every query below: applying a policy or queuing a refresh fans work out
  // through the outbox, and all of them have to keep reading until it lands.
  const fanOut = useFanOutWindow()

  const {
    data: work,
    isPending,
    isError,
    error: workError,
    refetch: refetchWork,
  } = useQuery({ queryKey: ['work', id], queryFn: () => catalogApi.get(id) })
  const seasons = useSeriesSeasons(id, fanOut)
  const { tree, applyPolicy } = useSeriesMonitoring(id, seasons.data?.length ?? 0, fanOut)
  const { data: assetByUnit } = useSeriesAssets(id)
  const { data: nextUp } = useNextUp(id)

  if (isPending) {
    return <SkeletonHero label="Loading title" />
  }
  // A 404 stays a 404: a title hidden by a collection the caller was not granted answers exactly like
  // one that does not exist, and that is deliberate — saying "you may not see this" would confirm it
  // exists. Every other failure is the API being unreachable, which is not the same thing and must
  // not be reported as a missing title.
  if (isError && !(workError instanceof ApiError && workError.status === 404)) {
    return (
      <div className="gutter py-16">
        <ErrorState
          title="This title could not be loaded"
          message={errorMessage(workError)}
          onRetry={() => void refetchWork()}
        />
      </div>
    )
  }
  if (isError || !work) {
    return (
      <div className="gutter py-16">
        <EmptyState
          title="This title could not be found."
          description="It may have been removed, or it sits in a collection you do not have access to."
          action={
            <ButtonLink to="/series" variant="subtle">
              Back to series
            </ButtonLink>
          }
        />
      </div>
    )
  }
  if (work.kind !== 'Series') {
    return <Navigate to={`/works/${id}`} replace />
  }

  const joined = joinSeasonsWithTargets(seasons.data ?? [], tree.data)

  return (
    <>
      <SeriesHero
        work={work}
        canManage={canManage}
        nextUp={nextUp ?? null}
        onPickArtwork={() => setPickingArtwork(true)}
        onRemove={() => setRemoving(true)}
        fanOut={fanOut}
      />

      <div className="gutter mt-4 max-w-5xl space-y-10 pb-16">
        {canManage && (
          <div className="space-y-3">
            <MonitoringModePicker
              seriesTitle={work.title}
              currentMode={tree.data?.root?.mode ?? null}
              totalEpisodeCount={tree.data?.root?.totalCount ?? null}
              pending={applyPolicy.isPending}
              onApply={(mode: MonitoringMode) => applyPolicy.mutateAsync(mode)}
            />
            {/* The picker deliberately swallows the rejection so the modal stays on the operator's
                choice; explaining it is this page's job, or the refusal would be invisible. */}
            {applyPolicy.isError && (
              <Alert tone="danger">{errorMessage(applyPolicy.error, 'The monitoring policy could not be applied.')}</Alert>
            )}
          </div>
        )}

        <section aria-labelledby="seasons-heading">
          <h2 id="seasons-heading" className="text-section text-fg">
            Seasons
          </h2>
          <div className="mt-4">
            {seasons.isPending ? (
              <div role="status" aria-label="Loading seasons" className="space-y-3">
                <Skeleton className="h-14" />
                <Skeleton className="h-14" />
                <Skeleton className="h-14" />
              </div>
            ) : seasons.isError ? (
              <ErrorState
                title="Seasons could not be loaded"
                message={errorMessage(seasons.error)}
                onRetry={() => void seasons.refetch()}
              />
            ) : joined.length === 0 ? (
              <EmptyState
                icon={<TvIcon className="size-9" />}
                title="No seasons yet"
                description={
                  canManage
                    ? 'Refresh this show’s metadata to pull its season and episode structure from a provider.'
                    : 'This show’s season and episode structure has not been synced yet.'
                }
              />
            ) : (
              <div className="border-b border-line-soft">
                {joined.map((season, index) => (
                  <SeasonAccordion
                    key={season.season.id}
                    workId={id}
                    season={season}
                    assetByUnit={assetByUnit}
                    canManage={canManage}
                    defaultOpen={index === 0}
                  />
                ))}
              </div>
            )}
          </div>
        </section>
      </div>

      {canManage && <RemoveWorkDialog work={work} open={removing} onClose={() => setRemoving(false)} />}

      {canManage && work.metadataSnapshotId && (
        <ArtworkPicker
          snapshotId={work.metadataSnapshotId}
          workId={id}
          open={pickingArtwork}
          onClose={() => setPickingArtwork(false)}
        />
      )}
    </>
  )
}

function SeriesHero({
  work,
  canManage,
  nextUp,
  onPickArtwork,
  onRemove,
  fanOut,
}: {
  work: Work
  canManage: boolean
  /** The viewer's next episode, as the playback module computed it; null when there is none to play. */
  nextUp: NextUpEpisode | null
  onPickArtwork: () => void
  onRemove: () => void
  fanOut: FanOutWindow
}) {
  const queryClient = useQueryClient()
  const refreshSource = refreshSourceFor(work.externalIds)

  const refresh = useMutation({
    mutationFn: (source: { provider: string; externalId: string }) =>
      metadataApi.refresh({ workId: work.id, provider: source.provider, externalId: source.externalId, kind: 'Series' }),
    onSuccess: async () => {
      // Enrichment and the structure sync run off the command queue; re-open the polling window and
      // let the series queries read until the tree stops growing.
      fanOut.extend()
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: seriesKeys.all(work.id) }),
        queryClient.invalidateQueries({ queryKey: ['work', work.id] }),
      ])
    },
  })

  const action = primaryActionFor({ work, nextUp })
  const menuItems: MenuItem[] = canManage
    ? [
        ...(refreshSource
          ? [
              {
                label: 'Refresh metadata',
                icon: <RefreshIcon />,
                onSelect: () => refresh.mutate(refreshSource),
                disabled: refresh.isPending,
              },
            ]
          : []),
        ...(work.metadataSnapshotId ? [{ label: 'Change artwork', icon: <ImageIcon />, onSelect: onPickArtwork }] : []),
        { label: 'Remove series', icon: <TrashIcon />, onSelect: onRemove, danger: true },
      ]
    : []
  const hasActions = action.kind !== 'details' || menuItems.length > 0

  return (
    <MediaHero
      work={work}
      showPoster
      back={{ to: '/series', label: 'Series' }}
      eyebrow={<WorkEyebrow work={work} />}
      overview={work.overview}
      status={<WorkAvailability work={work} />}
      actions={
        hasActions && (
          <>
            {action.kind !== 'details' && (
              <ButtonLink to={action.href} size="lg" icon={<PlayIcon className="size-5" />}>
                {action.label}
              </ButtonLink>
            )}
            {menuItems.length > 0 && (
              <Menu
                label={`More actions for ${work.title}`}
                items={menuItems}
                variant="overlay"
                size="icon-lg"
                align="start"
              />
            )}
          </>
        )
      }
    >
      {refresh.isError && (
        <Alert tone="danger" className="mt-4">
          {errorMessage(refresh.error, 'Could not queue a metadata refresh for this show.')}
        </Alert>
      )}
      {refresh.isSuccess && (
        <p role="status" className="mt-4 text-meta text-muted">
          Metadata refresh queued — this view updates as it lands.
        </p>
      )}
    </MediaHero>
  )
}
