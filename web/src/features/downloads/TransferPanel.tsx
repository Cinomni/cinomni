import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { downloadsApi } from '@/api/endpoints'
import type { DownloadTaskSummary, TunnelEgressStatus } from '@/api/types'
import { MediaStatus, StatusGlyph } from '@/components/media/MediaStatus'
import { DownloadDetail } from '@/features/downloads/DownloadDetail'
import { ApiError, errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { asPercent, formatRate } from '@/lib/format'
import { downloadLabel, downloadMediaState } from '@/lib/status'
import { tunnelPolicyLabel, tunnelReasonSentence } from '@/lib/tunnel'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { Checkbox } from '@/ui/Checkbox'
import { Menu } from '@/ui/Menu'
import { Modal } from '@/ui/Modal'

interface TransferPanelProps {
  task: DownloadTaskSummary
  /** Whether a network-held task may be resumed with an override. Undefined means "unknown": none is offered. */
  tunnel: TunnelEgressStatus | undefined
  /**
   * `nested` sits under the title it serves, so the torrent name is secondary; `standalone` is a
   * transfer with no title on the page, so its name is the heading.
   */
  variant?: 'nested' | 'standalone'
}

/**
 * One torrent as an operator reads and steers it: state, the progress line, the four transfer figures,
 * pause/resume (with the network-hold override when policy allows one), and file detail or removal
 * behind a menu. It owns its own dialogs so any page can drop it under whatever it belongs to.
 */
export function TransferPanel({ task, tunnel, variant = 'nested' }: TransferPanelProps) {
  const queryClient = useQueryClient()
  const [removing, setRemoving] = useState(false)
  const [viewingDetail, setViewingDetail] = useState(false)
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['downloads'] })

  const pause = useMutation({ mutationFn: () => downloadsApi.pause(task.id), onSuccess: invalidate })
  const resume = useMutation({
    mutationFn: (force: boolean) => downloadsApi.resume(task.id, force),
    onSuccess: invalidate,
  })

  const percent = asPercent(task.progress)
  const displayName = task.name || 'Resolving…'
  const isPaused = task.state === 'Paused'
  const isActive = task.state === 'Downloading' || task.state === 'Checking' || task.state === 'ResolvingMetadata'

  // The task summary already says whether it is held; a 409 on a plain resume would say the same
  // thing after a round trip that was doomed from the start, so this only exists to catch the task
  // becoming held between the last poll and the click.
  const refusedForNetworkHold =
    resume.isError && resume.error instanceof ApiError && resume.error.code === 'downloads.network_held'
  const isHeld = task.networkHeld || refusedForNetworkHold
  const canOverride = tunnel?.policy === 'PauseAndAlert'
  const otherResumeError = resume.isError && !refusedForNetworkHold ? errorMessage(resume.error) : null

  return (
    <div>
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <button
            type="button"
            onClick={() => setViewingDetail(true)}
            title={displayName}
            className={cn(
              'block max-w-full truncate text-left hover:text-accent-strong',
              variant === 'standalone' ? 'text-card font-semibold text-fg' : 'font-mono text-meta text-muted',
            )}
          >
            {displayName}
          </button>
          <div className="mt-1 flex flex-wrap items-center gap-x-4 gap-y-1">
            <MediaStatus state={downloadMediaState[task.state]} label={downloadLabel[task.state]} />
            {task.networkHeld && (
              <span className="inline-flex items-center gap-1.5 text-meta text-warning">
                <StatusGlyph state="missing" />
                Network held
              </span>
            )}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {isActive && (
            <Button size="sm" variant="ghost" loading={pause.isPending} onClick={() => pause.mutate()}>
              Pause
            </Button>
          )}
          {isPaused && !isHeld && (
            <Button size="sm" variant="ghost" loading={resume.isPending} onClick={() => resume.mutate(false)}>
              Resume
            </Button>
          )}
          {isPaused && isHeld && canOverride && (
            <Button size="sm" variant="ghost" loading={resume.isPending} onClick={() => resume.mutate(true)}>
              Resume anyway
            </Button>
          )}
          <Menu
            label={`Actions for ${task.name || 'this download'}`}
            size="icon-sm"
            items={[
              { label: 'Files & priorities', onSelect: () => setViewingDetail(true) },
              { label: 'Remove', danger: true, onSelect: () => setRemoving(true) },
            ]}
          />
        </div>
      </div>

      <div
        role="progressbar"
        aria-label={`${task.name || 'Download'} progress`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        className="mt-3 h-1 overflow-hidden rounded-full bg-line-soft"
      >
        <div
          className={cn(
            'h-full rounded-full transition-[width] duration-500 ease-out-quint',
            task.state === 'Error' ? 'bg-danger' : isPaused ? 'bg-faint' : 'bg-accent',
          )}
          style={{ width: `${percent}%` }}
        />
      </div>

      <TransferStats task={task} percent={percent} />

      {isPaused && isHeld && (
        <Alert tone="warning" className="mt-3">
          <p>
            This transfer is paused at the engine because torrent traffic could not be verified through the
            tunnel — it is held, not stalled and not broken.
          </p>
          {tunnel ? (
            <p className="mt-1">
              {tunnelReasonSentence(tunnel.reason)} On tunnel loss: {tunnelPolicyLabel[tunnel.policy]}.{' '}
              {!canOverride && 'Resuming is refused until egress verifies again.'}
            </p>
          ) : (
            <p className="mt-1">Tunnel policy could not be read, so an override is not offered here.</p>
          )}
        </Alert>
      )}

      {otherResumeError && (
        <Alert tone="danger" className="mt-3">
          {otherResumeError}
        </Alert>
      )}

      {/* Resume explains itself in detail; pausing said nothing at all when it was refused, and the
          row kept showing Downloading, which reads as the click having been ignored. */}
      {pause.isError && (
        <Alert tone="danger" className="mt-3">
          {errorMessage(pause.error, 'This transfer could not be paused.')}
        </Alert>
      )}

      {removing && <RemoveDownloadModal task={task} onClose={() => setRemoving(false)} />}
      {viewingDetail && (
        <DownloadDetail
          taskId={task.id}
          title={task.name || 'Download'}
          open
          onClose={() => setViewingDetail(false)}
        />
      )}
    </div>
  )
}

/**
 * The four figures an operator reads a transfer by, labelled and always visible — including on a
 * phone, where a bare "↓ · ↑" string was the first thing to be dropped. Rates the engine reports as
 * zero read as "—" rather than "0 B/s", so an idle direction does not look like a measured stall.
 */
function TransferStats({ task, percent }: { task: DownloadTaskSummary; percent: number }) {
  const stats: { label: string; value: string; tone?: string }[] = [
    { label: 'Progress', value: `${percent}%`, tone: 'text-fg' },
    {
      label: 'Download',
      value: `↓ ${formatRate(task.downloadRate)}`,
      tone: task.downloadRate > 0 ? 'text-accent' : undefined,
    },
    { label: 'Upload', value: `↑ ${formatRate(task.uploadRate)}`, tone: task.uploadRate > 0 ? 'text-info' : undefined },
    { label: 'Seeds · Peers', value: `${task.numSeeds} · ${task.numPeers}` },
  ]

  return (
    <dl className="mt-3 grid grid-cols-2 gap-x-6 gap-y-2 sm:grid-cols-4">
      {stats.map((stat) => (
        <div key={stat.label} className="min-w-0">
          <dt className="text-xs text-faint">{stat.label}</dt>
          <dd className={cn('truncate font-mono text-meta tabular-nums text-muted', stat.tone)}>{stat.value}</dd>
        </div>
      ))}
    </dl>
  )
}

function RemoveDownloadModal({ task, onClose }: { task: DownloadTaskSummary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [deleteFiles, setDeleteFiles] = useState(false)

  const remove = useMutation({
    mutationFn: () => downloadsApi.remove(task.id, deleteFiles),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['downloads'] })
      // Removing a transfer is fed back to the goal it served, so its row moves on as well.
      void queryClient.invalidateQueries({ queryKey: ['intents'] })
      onClose()
    },
  })

  return (
    <Modal open onClose={onClose} title="Remove download">
      <div className="space-y-4">
        <p className="text-sm text-fg">
          Remove <span className="font-medium">{task.name || 'this download'}</span> from the download client?
        </p>

        <Checkbox
          label="Also delete the downloaded files from disk"
          checked={deleteFiles}
          onChange={(event) => setDeleteFiles(event.target.checked)}
        />

        <Alert tone="warning">
          Deleting a partially downloaded file is not recoverable. Leave this unchecked to keep what has
          already been downloaded on disk.
        </Alert>

        {remove.isError && <Alert tone="danger">{errorMessage(remove.error)}</Alert>}

        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={remove.isPending}>
            Cancel
          </Button>
          <Button variant="danger" loading={remove.isPending} onClick={() => remove.mutate()}>
            Remove
          </Button>
        </div>
      </div>
    </Modal>
  )
}
