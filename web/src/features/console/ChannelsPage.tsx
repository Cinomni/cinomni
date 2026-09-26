import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { notificationsApi } from '@/api/endpoints'
import type { NotificationChannel, NotificationChannelKind } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { ErrorState } from '@/ui/ErrorState'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'
import { PlusIcon, TrashIcon } from '@/ui/icons'
import { Spinner } from '@/ui/Spinner'
import { TextField } from '@/ui/TextField'

export function ChannelsPage() {
  const [adding, setAdding] = useState(false)
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['channels'],
    queryFn: notificationsApi.channels,
  })

  return (
    <section>
      <div className="mb-4 flex items-end justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold tracking-tight">Notification channels</h2>
          <p className="mt-1 text-sm text-muted">Push notifications to a webhook or a Discord channel.</p>
        </div>
        <Button size="sm" icon={<PlusIcon className="size-4" />} onClick={() => setAdding(true)}>
          Add channel
        </Button>
      </div>

      {isPending ? (
        <div className="grid place-items-center py-10">
          <Spinner className="size-6 text-accent" />
        </div>
      ) : isError ? (
        // The list previously fell through to the "no channels" copy on a failed fetch, which told an
        // administrator nothing was configured when the real problem was an unreachable API.
        <ErrorState
          message={errorMessage(error, 'Could not load notification channels.')}
          onRetry={() => void refetch()}
        />
      ) : !data || data.length === 0 ? (
        <p className="rounded-panel border border-dashed border-line px-6 py-10 text-center text-sm text-muted">
          No channels yet. Add one to get notified outside the app.
        </p>
      ) : (
        <ul className="space-y-2">
          {data.map((channel) => (
            <ChannelRow key={channel.id} channel={channel} />
          ))}
        </ul>
      )}

      <AddChannelModal open={adding} onClose={() => setAdding(false)} />
    </section>
  )
}

function ChannelRow({ channel }: { channel: NotificationChannel }) {
  const queryClient = useQueryClient()
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['channels'] })

  const toggle = useMutation({
    mutationFn: () => notificationsApi.setChannelEnabled(channel.id, !channel.enabled),
    onSuccess: invalidate,
  })
  const remove = useMutation({
    mutationFn: () => notificationsApi.deleteChannel(channel.id),
    onMutate: () => setDeleteError(null),
    onSuccess: () => {
      invalidate()
      setConfirmingDelete(false)
    },
    onError: (err) => setDeleteError(errorMessage(err, 'Could not delete the channel.')),
  })

  return (
    <Card as="li" className="space-y-2">
      <div className="flex items-center justify-between gap-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <p className="font-medium text-fg">{channel.name}</p>
            <Badge tone="neutral">{channel.kind}</Badge>
            <Badge tone={channel.enabled ? 'success' : 'neutral'}>{channel.enabled ? 'Enabled' : 'Disabled'}</Badge>
          </div>
          <p className="mt-0.5 truncate font-mono text-xs text-faint">{channel.target}</p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <Button size="sm" variant="ghost" loading={toggle.isPending} onClick={() => toggle.mutate()}>
            {channel.enabled ? 'Disable' : 'Enable'}
          </Button>
          <Button
            size="sm"
            variant="danger"
            icon={<TrashIcon className="size-4" />}
            onClick={() => setConfirmingDelete(true)}
            aria-label={`Delete ${channel.name}`}
          />
        </div>
      </div>

      {toggle.isError && (
        <Alert tone="danger">
          {errorMessage(
            toggle.error,
            channel.enabled
              ? 'Could not disable the channel. It is still enabled.'
              : 'Could not enable the channel. It is still disabled.',
          )}
        </Alert>
      )}

      <Modal open={confirmingDelete} onClose={() => setConfirmingDelete(false)} title={`Delete ${channel.name}?`}>
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            Deleting <span className="font-medium text-fg">{channel.name}</span> is immediate and
            cannot be undone from Cinomni — the target address is not stored anywhere else in the
            app. To restore this channel later, you will need to re-enter its webhook or Discord
            URL from where you originally got it.
          </p>

          {deleteError && <Alert tone="danger">{deleteError}</Alert>}

          <div className="mt-1 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => setConfirmingDelete(false)}>
              Cancel
            </Button>
            <Button
              variant="danger"
              disabled={remove.isPending}
              loading={remove.isPending}
              onClick={() => remove.mutate()}
            >
              Delete channel
            </Button>
          </div>
        </div>
      </Modal>
    </Card>
  )
}

function AddChannelModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<NotificationChannelKind>('Discord')
  const [name, setName] = useState('')
  const [target, setTarget] = useState('')
  const [error, setError] = useState<string | null>(null)

  const add = useMutation({
    mutationFn: () => notificationsApi.addChannel({ kind, name, target }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['channels'] })
      onClose()
      setName('')
      setTarget('')
    },
    onError: (err) => setError(errorMessage(err, 'Could not add the channel.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    add.mutate()
  }

  return (
    <Modal open={open} onClose={onClose} title="Add channel">
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <Select label="Kind" value={kind} onChange={(e) => setKind(e.target.value as NotificationChannelKind)}>
          <option value="Discord">Discord</option>
          <option value="Webhook">Webhook</option>
        </Select>
        <TextField label="Name" required value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        <TextField
          label="Webhook URL"
          type="url"
          required
          placeholder={kind === 'Discord' ? 'https://discord.com/api/webhooks/…' : 'https://example.com/webhook'}
          value={target}
          onChange={(e) => setTarget(e.target.value)}
        />

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={add.isPending}>
            Add channel
          </Button>
        </div>
      </form>
    </Modal>
  )
}
