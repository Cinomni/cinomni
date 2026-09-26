import { Fragment, useId, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router'
import { catalogApi, libraryApi, playbackApi } from '@/api/endpoints'
import type { ClientCapability, PlaybackPreferences, PlaybackTicket } from '@/api/types'
import { ApiError, errorMessage } from '@/lib/api'
import { formatEpisodeCode } from '@/lib/format'
import { workPath } from '@/lib/routes'
import { Badge } from '@/ui/Badge'
import { ErrorState } from '@/ui/ErrorState'
import { SourceOffer } from '@/components/SourceOffer'
import { ChevronLeftIcon } from '@/ui/icons'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { detectCapability } from './capability'
import { audioLabel } from './trackLabels'
import { VideoStage } from './VideoStage'

// The device capability doesn't change during a session; detect it once per tab.
let cachedCapability: ClientCapability | null = null
function capability(): ClientCapability {
  cachedCapability ??= detectCapability()
  return cachedCapability
}

/** The title text and the route back to it — one lookup shared by the header and the failure states below. */
function useTitleInfo(assetId: string) {
  return useQuery({
    queryKey: ['playback-title', assetId],
    queryFn: async () => {
      const asset = await libraryApi.asset(assetId)
      const work = await catalogApi.get(asset.asset.workId)
      // An episode file plays one or more units of a series; the first names the session. A
      // multi-episode file is deliberately labelled by the episode it starts at.
      const unitId = work.kind === 'Series' ? asset.unitIds[0] : undefined
      const path = workPath(work)
      if (!unitId) return { label: work.title, path }
      const episode = await catalogApi.episode(work.id, unitId)
      return { label: `${work.title} · ${formatEpisodeCode(episode.seasonNumber, episode.number)}`, path }
    },
    staleTime: Infinity,
  })
}

export function PlayerPage() {
  const { assetId = '' } = useParams()
  const title = useTitleInfo(assetId)
  // A session that started can still fail mid-stream (network drop, decode error); that is a
  // different situation from one that never started, so it gets its own state and its own copy.
  const [streamFailed, setStreamFailed] = useState(false)
  // What the viewer chose — an audio track, a quality, or a seek past what a conversion reached. Null
  // until they choose anything: the first session is the server's choice entirely.
  const [preferences, setPreferences] = useState<PlaybackPreferences | null>(null)

  const {
    data: ticket,
    isFetching,
    isError,
    error,
    refetch,
  } = useQuery({
    queryKey: ['playback', assetId, preferences],
    queryFn: () =>
      playbackApi.start({ assetId, capability: capability(), ...(preferences ? { preferences } : {}) }),
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
    // Each fetch opens a new session — and, for a transcode, a new conversion on the server. Only the
    // viewer's own Retry may run it again; a blanket re-read (the realtime resync) must leave it alone.
    meta: { opensServerState: true },
  })

  const retry = () => {
    setStreamFailed(false)
    // A retry resumes from what the server last recorded, not from where an earlier switch began.
    if (preferences?.startPositionTicks !== undefined) {
      setPreferences({ ...preferences, startPositionTicks: undefined })
    } else {
      void refetch()
    }
  }

  const backHref = title.data?.path ?? '/'
  const backLabel = title.data?.path ? 'Back to title' : 'Back to library'

  return (
    <div className="flex h-dvh flex-col bg-black text-fg">
      <PlayerHeader titleLabel={title.data?.label} backHref={backHref} ticket={ticket} />
      <div className="flex min-h-0 flex-1 items-center justify-center">
        {isFetching ? (
          <LoadingBlock size="lg" label={preferences ? 'Switching the stream' : 'Starting playback'} />
        ) : isError || !ticket ? (
          <PlaybackFailure
            title={isBusy(error) ? 'No stream is free right now' : "This title can't be played"}
            message={errorMessage(error, 'The session could not be started.')}
            backHref={backHref}
            backLabel={backLabel}
            onRetry={retry}
          />
        ) : streamFailed ? (
          <PlaybackFailure
            title="Playback stopped"
            message="Something interrupted this session partway through. Retry to start a new one."
            backHref={backHref}
            backLabel={backLabel}
            onRetry={retry}
          />
        ) : (
          <VideoStage
            key={ticket.sessionId}
            ticket={ticket}
            onPlaybackError={() => setStreamFailed(true)}
            onRestart={setPreferences}
          />
        )}
      </div>
      <footer className="py-2 text-center text-xs text-faint">
        <SourceOffer />
      </footer>
    </div>
  )
}

/** A refusal because the server is converting as much as it may: a "not now", not a broken title. */
function isBusy(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'playback.transcode_limit'
}

function PlaybackFailure({
  title,
  message,
  backHref,
  backLabel,
  onRetry,
}: {
  title: string
  message: string
  backHref: string
  backLabel: string
  onRetry: () => void
}) {
  return (
    <div className="flex flex-col items-center gap-3">
      <ErrorState title={title} message={message} onRetry={onRetry} />
      <Link to={backHref} className="text-sm text-muted underline-offset-4 hover:text-fg hover:underline">
        {backLabel}
      </Link>
    </div>
  )
}

/** "1280×720 · up to 4 Mbps" — what a conversion produces, when something caps it. */
function outputLabel(ticket: PlaybackTicket): string | null {
  const { targetMaxWidth, targetMaxHeight, targetBitrateKbps } = ticket.plan
  const parts: string[] = []
  if (targetMaxHeight) parts.push(targetMaxWidth ? `${targetMaxWidth}×${targetMaxHeight}` : `${targetMaxHeight} lines`)
  if (targetBitrateKbps) parts.push(`up to ${targetBitrateKbps / 1000} Mbps`)
  return parts.length > 0 ? parts.join(' · ') : null
}

const OUTPUT_CODEC_LABEL: Readonly<Record<string, string>> = { H264: 'H.264', Hevc: 'HEVC' }
const AUDIO_CHANNELS_LABEL: Readonly<Record<number, string>> = { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }

/**
 * What else a conversion produces — codec, tone mapping, burned-in subtitles, folded audio — as
 * label/value rows. Sessions planned before these were recorded carry none of them and add no rows.
 */
function producedRows(ticket: PlaybackTicket): { term: string; value: string }[] {
  const { outputCodec, toneMapped, burnInSubtitleIndex, audioChannels } = ticket.plan
  const rows: { term: string; value: string }[] = []
  if (outputCodec) rows.push({ term: 'Video codec', value: OUTPUT_CODEC_LABEL[outputCodec] ?? outputCodec })
  if (toneMapped) rows.push({ term: 'Dynamic range', value: 'HDR → SDR' })
  if (burnInSubtitleIndex !== undefined && burnInSubtitleIndex !== null) {
    rows.push({ term: 'Subtitles', value: 'Burned in' })
  }
  if (audioChannels) {
    rows.push({ term: 'Audio channels', value: AUDIO_CHANNELS_LABEL[audioChannels] ?? `${audioChannels} channels` })
  }
  return rows
}

function PlayerHeader({
  titleLabel,
  backHref,
  ticket,
}: {
  titleLabel?: string
  backHref: string
  ticket?: PlaybackTicket
}) {
  const [showPlan, setShowPlan] = useState(false)
  const planPanelId = useId()
  const plan = ticket?.plan
  const audio = ticket?.selection.audio ?? null
  const audioTrack = ticket?.media?.audioTracks.find((t) => t.index === audio)
  const output = ticket ? outputLabel(ticket) : null
  const produced = ticket ? producedRows(ticket) : []

  return (
    <header className="relative z-20 flex items-center justify-between gap-3 bg-linear-to-b from-black/80 to-transparent px-4 py-3">
      <Link to={backHref} className="inline-flex items-center gap-1 text-sm text-muted transition-colors hover:text-fg">
        <ChevronLeftIcon className="size-5" /> Back
      </Link>
      {/* The only heading this route has: a player with no h1 leaves a screen reader with no
          statement of what is on screen. */}
      <h1 className="truncate text-sm font-medium">{titleLabel ?? 'Now playing'}</h1>
      <div className="flex items-center gap-2">
        {plan && (
          <button
            type="button"
            onClick={() => setShowPlan((v) => !v)}
            aria-expanded={showPlan}
            aria-controls={planPanelId}
            aria-label={`Why ${plan.method}?`}
          >
            <Badge tone={plan.method === 'DirectPlay' ? 'success' : 'info'}>{plan.method}</Badge>
          </button>
        )}
      </div>

      {showPlan && plan && (
        <div
          id={planPanelId}
          className="absolute right-4 top-full z-10 mt-1 w-80 rounded-panel border border-line bg-surface p-4 text-sm shadow-[var(--shadow-lift)]"
        >
          <p className="mb-2 font-medium">Why {plan.method}?</p>
          {plan.transcodeReasons.length > 0 ? (
            <ul className="list-inside list-disc space-y-1 text-muted">
              {plan.transcodeReasons.map((reason) => (
                <li key={reason}>{reason}</li>
              ))}
            </ul>
          ) : (
            <p className="text-muted">The file plays as-is on this device — no conversion needed.</p>
          )}

          {plan.method === 'Transcode' && plan.accelerationReasons.length > 0 && (
            <div className="mt-3 border-t border-line pt-3">
              <p className="mb-1 flex items-center justify-between gap-2 font-medium">
                Encoder
                <Badge tone={plan.backend === 'Software' ? 'neutral' : 'accent'}>
                  {plan.backend === 'Software' ? 'Software' : `${plan.backend} · hardware`}
                </Badge>
              </p>
              <ul className="list-inside list-disc space-y-1 text-muted">
                {plan.accelerationReasons.map((reason) => (
                  <li key={reason}>{reason}</li>
                ))}
              </ul>
            </div>
          )}

          <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 border-t border-line pt-3 text-xs">
            <dt className="text-faint">Audio track</dt>
            <dd className="text-right text-fg">
              {audio === null ? 'Default' : audioTrack ? audioLabel(audioTrack) : `Track ${audio}`}
            </dd>
            {output && (
              <>
                <dt className="text-faint">Converted to</dt>
                <dd className="text-right text-fg">{output}</dd>
              </>
            )}
            {produced.map((row) => (
              <Fragment key={row.term}>
                <dt className="text-faint">{row.term}</dt>
                <dd className="text-right text-fg">{row.value}</dd>
              </Fragment>
            ))}
          </dl>
        </div>
      )}
    </header>
  )
}
