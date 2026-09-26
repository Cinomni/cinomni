import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Navigate, useParams } from 'react-router'
import { catalogApi, libraryApi, metadataApi, monitoringApi, playbackApi } from '@/api/endpoints'
import type { MediaAssetSummary, MediaVersion, Work } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { MediaHero } from '@/components/media/MediaHero'
import { MediaStatus } from '@/components/media/MediaStatus'
import { primaryActionFor } from '@/components/media/primaryAction'
import { summarizeStreams } from '@/components/media/quality'
import { WorkAvailability, WorkEyebrow } from '@/components/media/WorkFacts'
import { ApiError, errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatBytes } from '@/lib/format'
import { refreshSourceFor } from '@/lib/providers'
import { InteractiveSearchDialog } from '@/features/search/InteractiveSearchDialog'
import { SubtitlePanel } from '@/features/subtitles/SubtitlePanel'
import { Alert } from '@/ui/Alert'
import { Button, ButtonLink } from '@/ui/Button'
import { Disclosure } from '@/ui/Disclosure'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { EyeIcon, EyeOffIcon, ImageIcon, PlayIcon, RefreshIcon, SearchIcon, TrashIcon } from '@/ui/icons'
import { Menu, type MenuItem } from '@/ui/Menu'
import { Skeleton, SkeletonHero } from '@/ui/Skeleton'
import { ArtworkPicker } from './ArtworkPicker'
import { RemoveWorkDialog } from './RemoveWorkDialog'

export function WorkDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  // Monitoring, refreshing and artwork are operator actions — the API refuses them for anyone else.
  const isAdmin = user?.isAdministrator ?? false
  const [pickingArtwork, setPickingArtwork] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [searching, setSearching] = useState(false)

  const {
    data: work,
    isPending,
    isError,
    error: workError,
    refetch: refetchWork,
  } = useQuery({ queryKey: ['work', id], queryFn: () => catalogApi.get(id) })

  const assets = useQuery({
    queryKey: ['assets', 'work', id],
    queryFn: () => libraryApi.assets(id),
  })

  const target = useQuery({
    queryKey: ['target', 'work', id],
    queryFn: async () => {
      try {
        return await monitoringApi.targetForWork(id)
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null
        throw error
      }
    },
  })

  const playableAsset = assets.data?.find((a) => a.state === 'Active') ?? assets.data?.[0]

  // Where this viewer stopped, so the hero can offer to resume rather than start over.
  const resume = useQuery({
    queryKey: ['playback', 'progress', playableAsset?.id],
    queryFn: async () => (playableAsset ? ((await playbackApi.resume(playableAsset.id)) ?? null) : null),
    enabled: playableAsset != null,
  })


  const monitor = useMutation({
    mutationFn: async () => {
      if (target.data) return monitoringApi.setMonitored(target.data.id, !target.data.monitored)
      await monitoringApi.applyPolicy(id, 'All')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['target', 'work', id] }),
  })

  const refresh = useMutation({
    mutationFn: (t: { provider: string; externalId: string }) =>
      metadataApi.refresh({ workId: id, provider: t.provider, externalId: t.externalId, kind: 'Movie' }),
    onSuccess: () => {
      // Enrichment runs off the command queue/outbox; pick it up shortly.
      setTimeout(() => {
        void queryClient.invalidateQueries({ queryKey: ['work', id] })
        void queryClient.invalidateQueries({ queryKey: ['works'] })
      }, 2500)
    },
  })

  if (isPending) {
    return <SkeletonHero label="Loading title" />
  }
  // A 404 stays a 404: a title hidden by a collection the caller was not granted answers exactly like
  // one that does not exist, and that is deliberate — saying "you may not see this" would confirm it
  // exists. Every other failure is the API being unreachable, which is not the same thing and must
  // not be reported as a missing title. Same rule as the series page.
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
          action={<ButtonLink to="/movies" variant="subtle">Back to movies</ButtonLink>}
        />
      </div>
    )
  }
  // A series has its own surface. Old links, bookmarks and any caller that only knows a work id land
  // here, so this page forwards rather than rendering a show as if it were a single file.
  if (work.kind === 'Series') {
    return <Navigate to={`/series/${id}`} replace />
  }

  const refreshTarget = refreshSourceFor(work.externalIds)
  const isMonitored = target.data?.monitored ?? false
  const action = primaryActionFor({
    work,
    playableAssetId: playableAsset?.id,
    resumePositionTicks: resume.data?.positionTicks,
  })
  const canPlay = action.kind !== 'details'

  const menuItems: MenuItem[] = isAdmin
    ? [
        // Searching needs a target: it is what carries the criterion and the goal.
        ...(target.data && canPlay
          ? [{ label: 'Find a better release', icon: <SearchIcon />, onSelect: () => setSearching(true) }]
          : []),
        ...(refreshTarget
          ? [{ label: 'Refresh metadata', icon: <RefreshIcon />, onSelect: () => refresh.mutate(refreshTarget), disabled: refresh.isPending }]
          : []),
        ...(work.metadataSnapshotId
          ? [{ label: 'Change artwork', icon: <ImageIcon />, onSelect: () => setPickingArtwork(true) }]
          : []),
        { label: 'Remove movie', icon: <TrashIcon />, onSelect: () => setRemoving(true), danger: true },
      ]
    : []

  return (
    <>
      <MediaHero
        work={work}
        showPoster
        back={{ to: '/movies', label: 'Movies' }}
        eyebrow={<WorkEyebrow work={work} />}
        overview={work.overview}
        status={
          <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
            <WorkAvailability work={work} />
            {/* An operator reads this off the Monitor toggle itself. */}
            {!isAdmin && isMonitored && <MediaStatus state="monitored" label="Monitored" />}
          </div>
        }
        actions={
          <>
            {canPlay && (
              <ButtonLink to={action.href} size="lg" icon={<PlayIcon className="size-5" />}>
                {action.label}
              </ButtonLink>
            )}
            {/* Nothing on disk yet: finding a release is the most useful thing an operator can do here. */}
            {isAdmin && !canPlay && target.data && (
              <Button size="lg" icon={<SearchIcon className="size-5" />} onClick={() => setSearching(true)}>
                Find releases
              </Button>
            )}
            {isAdmin && (
              <Button
                variant="overlay"
                size="lg"
                aria-pressed={isMonitored}
                loading={monitor.isPending}
                icon={isMonitored ? <EyeIcon className="size-5" /> : <EyeOffIcon className="size-5" />}
                onClick={() => monitor.mutate()}
              >
                {isMonitored ? 'Monitoring' : 'Monitor'}
              </Button>
            )}
            {menuItems.length > 0 && (
              <Menu label={`More actions for ${work.title}`} items={menuItems} variant="overlay" size="icon-lg" align="start" />
            )}
          </>
        }
      >
        {monitor.isError && (
          <Alert tone="danger" className="mt-4">
            {errorMessage(monitor.error, 'Could not update monitoring for this title.')}
          </Alert>
        )}
        {refresh.isError && (
          <Alert tone="danger" className="mt-4">
            {errorMessage(refresh.error, 'Could not queue a metadata refresh for this title.')}
          </Alert>
        )}
        {refresh.isSuccess && (
          <p role="status" className="mt-4 text-meta text-muted">
            Metadata refresh queued — this view updates shortly.
          </p>
        )}
      </MediaHero>

      <div className="gutter mt-4 max-w-5xl space-y-10 pb-16">
        {target.isError && (
          <ErrorState
            title="Monitoring status could not be loaded"
            message={errorMessage(target.error)}
            onRetry={() => void target.refetch()}
          />
        )}

        <AssetPanel assets={assets} work={work} isAdmin={isAdmin} />
      </div>

      {isAdmin && target.data && (
        <InteractiveSearchDialog
          targetId={target.data.id}
          title={work.title}
          open={searching}
          onClose={() => setSearching(false)}
        />
      )}

      {isAdmin && <RemoveWorkDialog work={work} open={removing} onClose={() => setRemoving(false)} />}

      {isAdmin && work.metadataSnapshotId && (
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

/** The subset of `useQuery`'s result `AssetPanel` needs — the whole object would leak query internals. */
interface AssetsQueryView {
  data: MediaAssetSummary[] | undefined
  isPending: boolean
  isError: boolean
  error: unknown
  refetch: () => unknown
}

/**
 * The file behind the title, in two layers: what a viewer cares about (picture, sound, subtitle
 * languages) always visible, and what an operator diagnoses with (paths, versions, raw streams,
 * release group, subtitle searches, provider ids) folded underneath.
 */
function AssetPanel({ assets, work, isAdmin }: { assets: AssetsQueryView; work: Work; isAdmin: boolean }) {
  const asset = assets.data?.find((a) => a.state === 'Active') ?? assets.data?.[0]

  const detail = useQuery({
    queryKey: ['asset', asset?.id],
    queryFn: () => (asset ? libraryApi.asset(asset.id) : Promise.reject(new Error('No asset'))),
    enabled: !!asset,
  })

  if (assets.isPending) {
    return (
      <div role="status" aria-label="Loading media files" className="space-y-3">
        <Skeleton className="h-5 w-32" />
        <Skeleton className="h-4 w-72" />
      </div>
    )
  }

  if (assets.isError) {
    return (
      <ErrorState
        title="Media files could not be loaded"
        message={errorMessage(assets.error)}
        onRetry={() => void assets.refetch()}
      />
    )
  }

  if (!asset) {
    return (
      <section className="space-y-2">
        <h2 className="text-section text-fg">Not in your library yet</h2>
        <p className="max-w-xl text-meta text-muted">
          {isAdmin
            ? 'No file has been imported for this movie. Monitor it and Cinomni searches for a release, downloads it and imports it on its own — or pick a release yourself.'
            : 'No file has been imported for this movie yet. It appears here, ready to play, once it has been downloaded.'}
        </p>
      </section>
    )
  }

  const versions = detail.data?.versions ?? []
  const primary = versions[0]
  const quality = primary ? summarizeStreams(primary.streams) : null

  return (
    <>
      <section aria-labelledby="playback-heading" className="space-y-4">
        <h2 id="playback-heading" className="text-section text-fg">
          Playback
        </h2>
        {detail.isPending ? (
          <div role="status" aria-label="Loading file details" className="grid gap-4 sm:grid-cols-3">
            <Skeleton className="h-12" />
            <Skeleton className="h-12" />
            <Skeleton className="h-12" />
          </div>
        ) : detail.isError ? (
          <ErrorState
            title="File details could not be loaded"
            message={errorMessage(detail.error)}
            onRetry={() => void detail.refetch()}
          />
        ) : !quality ? (
          <p className="text-meta text-muted">Registered in the library.</p>
        ) : (
          <dl className="grid gap-x-8 gap-y-4 sm:grid-cols-3">
            <Fact term="Picture" value={quality.video ?? 'Unknown'} />
            <Fact term="Sound" value={quality.audio ?? 'Unknown'} />
            <Fact
              term="Subtitles"
              value={quality.subtitles.length > 0 ? quality.subtitles.join(', ') : 'None in the file'}
            />
          </dl>
        )}
      </section>

      <div>
        <Disclosure title="Subtitles" hint="Searches for this file and their outcome">
          <SubtitlePanel assetId={asset.id} />
        </Disclosure>
        <Disclosure
          title="Advanced"
          hint="Files, streams, release group and provider ids"
          className="border-b"
        >
          <div className="space-y-6">
            {versions.length > 0 && (
              // An upgraded file leaves its previous version registered until it is pruned, so a work can
              // legitimately carry more than one — showing only versions[0] would hide the others silently.
              <div className="space-y-5">
                {versions.map((version, index) => (
                  <VersionDetail key={version.id} version={version} current={index === 0} />
                ))}
              </div>
            )}
            {work.externalIds.length > 0 && (
              <div>
                <p className="text-label uppercase text-faint">Provider ids</p>
                <p className="mt-1.5 flex flex-wrap gap-x-4 gap-y-1 font-mono text-meta text-muted">
                  {work.externalIds.map((ext) => (
                    <span key={`${ext.provider}-${ext.value}`}>
                      {ext.provider}: {ext.value}
                    </span>
                  ))}
                </p>
              </div>
            )}
          </div>
        </Disclosure>
      </div>
    </>
  )
}

function Fact({ term, value }: { term: string; value: string }) {
  return (
    <div>
      <dt className="text-label uppercase text-faint">{term}</dt>
      <dd className="mt-1 text-body text-fg">{value}</dd>
    </div>
  )
}

function VersionDetail({ version, current }: { version: MediaVersion; current: boolean }) {
  return (
    <div className="space-y-2">
      <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-meta">
        <span className={cn('text-label uppercase', current ? 'text-accent' : 'text-faint')}>
          {current ? 'Current file' : 'Previous version'}
        </span>
        <span className="text-muted">{formatBytes(version.size)}</span>
        {version.releaseGroup && <span className="text-muted">Release group {version.releaseGroup}</span>}
      </p>
      <p className="break-all font-mono text-meta text-muted">{version.fullPath ?? version.relativePath}</p>
      <ul className="flex flex-col gap-1 font-mono text-meta text-faint">
        {version.streams.map((stream) => (
          <li key={stream.streamIndex}>
            #{stream.streamIndex} {stream.type}
            {stream.codec ? ` · ${stream.codec}` : ''}
            {stream.height ? ` · ${stream.width ?? '?'}×${stream.height}` : ''}
            {stream.videoRangeType && stream.videoRangeType !== 'Sdr' ? ` · ${stream.videoRangeType}` : ''}
            {stream.channels ? ` · ${stream.channels} ch` : ''}
            {stream.language ? ` · ${stream.language}` : ''}
            {stream.isDefault ? ' · default' : ''}
            {stream.isForced ? ' · forced' : ''}
            {stream.isExternal ? ' · external' : ''}
          </li>
        ))}
      </ul>
    </div>
  )
}
