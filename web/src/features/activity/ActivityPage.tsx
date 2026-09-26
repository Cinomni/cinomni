import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { acquisitionApi, catalogApi, downloadsApi, monitoringApi } from '@/api/endpoints'
import type {
  AcquisitionIntentSummary,
  DownloadTaskSummary,
  IntentState,
  MonitoredTarget,
  TunnelEgressStatus,
  Work,
} from '@/api/types'
import { PageHeader } from '@/components/PageHeader'
import { PosterArt } from '@/components/media/MediaPoster'
import { PipelineSteps } from '@/components/media/PipelineSteps'
import { TransferPanel } from '@/features/downloads/TransferPanel'
import { errorMessage } from '@/lib/api'
import { formatEpisodeCode } from '@/lib/format'
import { workPath } from '@/lib/routes'
import { intentLabel, intentPipeline } from '@/lib/status'
import { useFallbackRefetchInterval } from '@/realtime/useRealtimeStatus'
import { Alert } from '@/ui/Alert'
import { Button, ButtonLink } from '@/ui/Button'
import { ActivityIcon } from '@/ui/icons'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { Skeleton } from '@/ui/Skeleton'
import { IntentDetail } from './IntentDetail'

type Group = 'attention' | 'progress' | 'done'

/** Which heading an intent sits under. Failure first: it is the only group that needs a person. */
const GROUP_OF: Record<IntentState, Group> = {
  Exhausted: 'attention',
  Requested: 'progress',
  Planned: 'progress',
  Searching: 'progress',
  CandidateSelected: 'progress',
  Downloading: 'progress',
  Importing: 'progress',
  Available: 'done',
  Cancelled: 'done',
}

const GROUPS: readonly { key: Group; title: string; description: string }[] = [
  {
    key: 'attention',
    title: 'Needs attention',
    description: 'Every attempt was used without an acceptable release. Open the attempts to see why, or search yourself.',
  },
  { key: 'progress', title: 'In progress', description: 'Searching, downloading or importing right now.' },
  { key: 'done', title: 'Done', description: 'Reached the library.' },
]

export function ActivityPage() {
  // Every acquisition step announces itself on the live stream; the interval is the fallback for when
  // that stream is not up.
  const {
    data: intents,
    isPending: intentsPending,
    isError: intentsError,
    error: intentsErrorObj,
    refetch: refetchIntents,
  } = useQuery({
    queryKey: ['intents'],
    queryFn: acquisitionApi.intents,
    refetchInterval: useFallbackRefetchInterval(4_000),
  })
  const {
    data: works,
    isError: worksError,
    error: worksErrorObj,
    refetch: refetchWorks,
  } = useQuery({ queryKey: ['works', null], queryFn: () => catalogApi.list() })

  // An intent only carries a work id, so the destination depends on the work's kind: a series has its
  // own page and linking it to the movie-shaped one would hide its seasons entirely.
  const workFor = useMemo(() => {
    const map = new Map(works?.map((w) => [w.id, w]))
    return (workId: string) => map.get(workId) ?? null
  }, [works])

  // Which episode or season an intent is for: a series fans out one intent per episode, and a page of
  // rows that all read "Severance" says nothing. An enrichment only — without it a row still names
  // its title and state.
  const { data: targets } = useQuery({ queryKey: ['monitoring', 'targets'], queryFn: monitoringApi.targets })
  const targetById = useMemo(() => new Map(targets?.map((t) => [t.id, t])), [targets])

  // The torrents behind those titles. Their figures arrive on the live stream, which writes straight
  // into this cache entry; the interval is only what happens when that stream is down. A failure here
  // costs the figures, never the titles — the page still says where every goal stands.
  const transfers = useQuery({
    queryKey: ['downloads'],
    queryFn: downloadsApi.list,
    refetchInterval: useFallbackRefetchInterval(3_000),
  })
  // Needed to know whether a network-held task may be resumed with an override: PauseAndAlert allows
  // one, Block never does. Unknown means no override is offered.
  const tunnel = useQuery({
    queryKey: ['downloads', 'tunnel'],
    queryFn: downloadsApi.tunnel,
    refetchInterval: useFallbackRefetchInterval(15_000),
  })

  // Which torrents each goal is waiting on. A season pack serves several goals, so it appears under
  // each of them; a torrent no listed goal claims still has to be reachable, so it is kept aside.
  const { transfersFor, unclaimed } = useMemo(() => {
    const byIntent = new Map<string, DownloadTaskSummary[]>()
    for (const task of transfers.data ?? []) {
      for (const intentId of task.intentIds) {
        byIntent.set(intentId, [...(byIntent.get(intentId) ?? []), task])
      }
    }
    const listed = new Set(intents?.map((intent) => intent.id))
    return {
      transfersFor: (intentId: string) => byIntent.get(intentId) ?? [],
      unclaimed: (transfers.data ?? []).filter((task) => !task.intentIds.some((id) => listed.has(id))),
    }
  }, [transfers.data, intents])

  const [selectedIntent, setSelectedIntent] = useState<AcquisitionIntentSummary | null>(null)

  return (
    <>
      <PageHeader
        title="Activity"
        subtitle="Every monitored title on its way to your library, with its transfer as it happens."
      />

      {transfers.isError && (
        <Alert tone="warning" className="mb-6">
          {errorMessage(transfers.error, 'Live transfer figures could not be read.')} Titles below still show
          where each one stands.
        </Alert>
      )}

      {intentsPending ? (
        <div role="status" aria-label="Loading activity" className="space-y-4">
          {Array.from({ length: 4 }, (_, index) => (
            <Skeleton key={index} className="h-16" />
          ))}
        </div>
      ) : intentsError ? (
        <ErrorState
          title="Could not load activity"
          message={errorMessage(intentsErrorObj)}
          onRetry={() => void refetchIntents()}
        />
      ) : worksError ? (
        // Titles come from the works list; without it we cannot say which work an intent belongs to,
        // so this must not silently degrade into "Unknown title" rows.
        <ErrorState
          title="Could not load titles"
          message={errorMessage(worksErrorObj)}
          onRetry={() => void refetchWorks()}
        />
      ) : intents.length === 0 && unclaimed.length === 0 ? (
        <EmptyState
          icon={<ActivityIcon className="size-9" />}
          title="Nothing in progress"
          description="When a monitored title starts searching, downloading or importing, it shows up here — with every step it takes on the way."
          action={
            <ButtonLink to="/console/wanted" variant="subtle">
              See what is wanted
            </ButtonLink>
          }
        />
      ) : (
        <div className="space-y-12">
          {GROUPS.map((group) => {
            const rows = intents.filter((intent) => GROUP_OF[intent.state] === group.key)
            if (rows.length === 0) return null
            return (
              <section key={group.key} aria-labelledby={`activity-${group.key}`}>
                <div className="mb-2 flex items-baseline gap-3">
                  <h2 id={`activity-${group.key}`} className="text-section text-fg">
                    {group.title}
                  </h2>
                  <span className="text-meta tabular-nums text-faint">{rows.length}</span>
                </div>
                <p className="mb-4 text-meta text-muted">{group.description}</p>
                <ul className="divide-y divide-line-soft">
                  {rows.map((intent) => (
                    <IntentRow
                      key={intent.id}
                      intent={intent}
                      work={workFor(intent.workId)}
                      target={targetById.get(intent.targetId)}
                      transfers={transfersFor(intent.id)}
                      transfersKnown={transfers.isSuccess}
                      tunnel={tunnel.data}
                      onOpen={() => setSelectedIntent(intent)}
                    />
                  ))}
                </ul>
              </section>
            )
          })}

          {unclaimed.length > 0 && (
            <section aria-labelledby="activity-other-transfers">
              <div className="mb-2 flex items-baseline gap-3">
                <h2 id="activity-other-transfers" className="text-section text-fg">
                  Other transfers
                </h2>
                <span className="text-meta tabular-nums text-faint">{unclaimed.length}</span>
              </div>
              <p className="mb-4 text-meta text-muted">
                Torrents in the download client that no title above is waiting on.
              </p>
              <ul className="divide-y divide-line-soft">
                {unclaimed.map((task) => (
                  <li key={task.id} className="py-4">
                    <TransferPanel task={task} tunnel={tunnel.data} variant="standalone" />
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>
      )}

      {selectedIntent && (
        <IntentDetail
          intentId={selectedIntent.id}
          title={[workFor(selectedIntent.workId)?.title ?? 'Unknown title', unitLabel(targetById.get(selectedIntent.targetId))]
            .filter(Boolean)
            .join(' ')}
          open
          onClose={() => setSelectedIntent(null)}
        />
      )}
    </>
  )
}

/** "S01E03 · In Perpetuity", "Season 2", or nothing for a movie. */
function unitLabel(target: MonitoredTarget | undefined): string | null {
  if (!target || target.seasonNumber == null) return null
  if (target.kind === 'Season') return target.seasonNumber === 0 ? 'Specials' : `Season ${target.seasonNumber}`
  const code = formatEpisodeCode(target.seasonNumber, target.episodeNumber)
  return target.title ? `${code} · ${target.title}` : code
}

function IntentRow({
  intent,
  work,
  target,
  transfers,
  transfersKnown,
  tunnel,
  onOpen,
}: {
  intent: AcquisitionIntentSummary
  work: Work | null
  target: MonitoredTarget | undefined
  transfers: readonly DownloadTaskSummary[]
  /** Whether the transfer list was read; only then can "none" be stated rather than assumed. */
  transfersKnown: boolean
  tunnel: TunnelEgressStatus | undefined
  onOpen: () => void
}) {
  const unit = unitLabel(target)
  const title = unit ? `${work?.title ?? 'Unknown title'} ${unit}` : (work?.title ?? 'Unknown title')
  // The goal says Downloading, yet the client holds nothing for it: the torrent was removed or lost.
  // Left silent, the row reads as a download that simply shows no figures.
  const isMissingTransfer = intent.state === 'Downloading' && transfersKnown && transfers.length === 0
  return (
    <li className="py-4">
      <IntentSummaryLine intent={intent} work={work} unit={unit} title={title} onOpen={onOpen} />
      {isMissingTransfer && (
        <Alert tone="warning" className="mt-4 sm:ml-15">
          No torrent is running for this title in the download client, so there is nothing to show progress
          for. Open its attempts to see what happened to the last one.
        </Alert>
      )}
      {transfers.length > 0 && (
        // Indented to the text column, past the poster, so the torrent reads as belonging to this title.
        <ul aria-label={`Transfers for ${title}`} className="mt-4 space-y-4 sm:pl-15">
          {transfers.map((task) => (
            <li key={task.id} className="rounded-panel bg-surface p-3">
              <TransferPanel task={task} tunnel={tunnel} />
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

function IntentSummaryLine({
  intent,
  work,
  unit,
  title,
  onOpen,
}: {
  intent: AcquisitionIntentSummary
  work: Work | null
  unit: string | null
  title: string
  onOpen: () => void
}) {
  return (
    <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:gap-6">
      <div className="flex min-w-0 flex-1 items-center gap-4">
        <span className="h-16 w-11 shrink-0 overflow-hidden rounded-media bg-elevated">
          {work && <PosterArt work={work} />}
        </span>
        <div className="min-w-0">
          <Link
            to={workPath(work ?? { id: intent.workId, kind: 'Movie' })}
            className="block truncate text-card font-semibold text-fg hover:text-accent-strong"
          >
            {work?.title ?? 'Unknown title'}
          </Link>
          {unit && <p className="truncate font-mono text-meta text-fg/80">{unit}</p>}
          <p className={intent.state === 'Exhausted' ? 'text-meta text-danger' : 'text-meta text-muted'}>
            {intentLabel[intent.state]}
          </p>
          <p className="text-meta tabular-nums text-faint">
            Attempt {intent.attemptCount} of {intent.maxAttempts}
            {intent.selectedReleaseGuid ? ' · release selected' : ''}
          </p>
        </div>
      </div>
      <PipelineSteps position={intentPipeline[intent.state]} className="sm:w-96" />
      <Button size="sm" variant="ghost" onClick={onOpen} className="self-start sm:self-center">
        Attempts
        <span className="sr-only"> for {title}</span>
      </Button>
    </div>
  )
}
