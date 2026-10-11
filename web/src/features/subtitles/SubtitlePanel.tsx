import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { subtitlesApi } from '@/api/endpoints'
import type { SubtitleSearchSummary } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { subtitleTone } from '@/lib/status'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { syncBadge } from './subtitleSync'

const COLUMNS: Column<SubtitleSearchSummary>[] = [
  { key: 'language', header: 'Language', render: (row) => row.language },
  { key: 'forced', header: 'Forced', render: (row) => (row.forced ? 'Yes' : 'No') },
  {
    key: 'hearingImpaired',
    header: 'Hearing-impaired',
    render: (row) => (row.hearingImpaired ? 'Yes' : 'No'),
  },
  {
    key: 'state',
    header: 'State',
    render: (row) => <Badge tone={subtitleTone[row.state]}>{row.state}</Badge>,
  },
  {
    key: 'timing',
    header: 'Timing',
    render: (row) => {
      const badge = syncBadge(row)
      return badge ? <Badge tone={badge.tone}>{badge.label}</Badge> : <span className="text-faint">—</span>
    },
  },
  { key: 'attempts', header: 'Attempts', render: (row) => row.attempts, align: 'end' },
]

/**
 * Subtitle search status for one media file. `subtitlesApi.forAsset` is already scoped server-side
 * to what the calling viewer may see, so this panel is safe to render for any signed-in account: it
 * shows exactly what the API returns and nothing derived from a file path.
 *
 * An empty result and a search that failed after several attempts are deliberately not the same
 * screen. An empty list means Cinomni has never recorded a subtitle search for this file; a row
 * with state `NotFound` is a completed search that came up without an acceptable match. Collapsing
 * both into "no subtitles" would claim something the data does not say.
 *
 * Timing is decided by the backend, which records why. This panel names the decision and quotes the
 * reason; an administrator may ask for another check or put the downloaded file back. Hiding those
 * buttons from everybody else is a courtesy — the endpoints are administrator-only.
 */
export function SubtitlePanel({ assetId, canManage = false }: { assetId: string; canManage?: boolean }) {
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['subtitles', 'asset', assetId],
    queryFn: () => subtitlesApi.forAsset(assetId),
  })

  const landed = data?.filter((row) => row.subtitle !== null) ?? []

  return (
    <Card as="section" className="space-y-3">
      <h2 className="font-medium text-fg">Subtitles</h2>

      {isPending ? (
        <LoadingBlock size="sm" label="Loading subtitle searches" />
      ) : isError ? (
        <ErrorState
          title="Subtitle status could not be loaded"
          message={errorMessage(error)}
          onRetry={() => void refetch()}
        />
      ) : data.length === 0 ? (
        <EmptyState
          title="No subtitle search recorded"
          description="Cinomni has not searched for subtitles for this file yet — this is not the same as confirming none exist."
        />
      ) : (
        <>
          <DataTable
            columns={COLUMNS}
            rows={data}
            rowKey={(row) => row.id}
            caption="Subtitle searches for this file"
          />
          <p className="text-xs text-faint">
            One row per subtitle search Cinomni has run for this file. A "NotFound" state means the search
            completed without an acceptable match — it does not mean no subtitles exist anywhere.
          </p>
          {landed.length > 0 && (
            <ul aria-label="Subtitle timing" className="space-y-3">
              {landed.map((row) => (
                <SyncDetail key={row.id} assetId={assetId} search={row} canManage={canManage} />
              ))}
            </ul>
          )}
        </>
      )}
    </Card>
  )
}

/** One landed subtitle's timing: what was decided and why, plus the administrator's two actions. */
function SyncDetail({
  assetId,
  search,
  canManage,
}: {
  assetId: string
  search: SubtitleSearchSummary
  canManage: boolean
}) {
  const queryClient = useQueryClient()
  const [confirmingRevert, setConfirmingRevert] = useState(false)
  const subtitle = search.subtitle
  const sync = subtitle?.sync ?? null
  const badge = syncBadge(search)
  const syncing = search.state === 'Syncing'
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['subtitles', 'asset', assetId] })

  const resync = useMutation({
    mutationFn: (id: string) => subtitlesApi.resync(id),
    onSuccess: invalidate,
  })
  const revert = useMutation({
    mutationFn: (id: string) => subtitlesApi.revert(id),
    onSuccess: () => {
      setConfirmingRevert(false)
      return invalidate()
    },
  })

  if (!subtitle) return null
  const name = `${search.language}${search.forced ? ' (forced)' : ''}${search.hearingImpaired ? ' (HI)' : ''}`
  const failure = resync.error ?? revert.error

  return (
    <li className="space-y-1.5">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium text-fg">{name}</span>
        {badge && <Badge tone={badge.tone}>{badge.label}</Badge>}
      </div>
      {sync && <p className="text-xs text-muted">{sync.reason}</p>}
      {syncing && !sync && (
        <p className="text-xs text-muted">The subtitle is playable now; its timing is being checked against the audio.</p>
      )}
      {canManage && (
        <div className="flex flex-wrap items-center gap-2">
          <Button
            size="sm"
            variant="subtle"
            loading={resync.isPending}
            disabled={syncing || revert.isPending}
            onClick={() => resync.mutate(subtitle.id)}
            aria-label={`Re-sync ${name} subtitle`}
          >
            Re-sync
          </Button>
          {sync?.hasOriginal &&
            (confirmingRevert ? (
              <>
                <span className="text-xs text-muted">Play the downloaded file again?</span>
                <Button
                  size="sm"
                  variant="danger"
                  loading={revert.isPending}
                  onClick={() => revert.mutate(subtitle.id)}
                >
                  Confirm revert
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setConfirmingRevert(false)}>
                  Cancel
                </Button>
              </>
            ) : (
              <Button
                size="sm"
                variant="ghost"
                disabled={syncing || resync.isPending}
                onClick={() => setConfirmingRevert(true)}
                aria-label={`Revert ${name} subtitle to the downloaded file`}
              >
                Revert
              </Button>
            ))}
        </div>
      )}
      {failure && (
        <p role="alert" className="text-xs text-danger">
          {errorMessage(failure)}
        </p>
      )}
    </li>
  )
}
