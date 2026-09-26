import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { catalogApi } from '@/api/endpoints'
import type { Collection, RulePreview } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Modal } from '@/ui/Modal'
import { RulePreviewSummary } from './RulePreviewSummary'
import { ChevronDownIcon, ChevronRightIcon } from '@/ui/icons'

/** A copy of `list` with the entry at `index` moved one place in `direction`. Never mutates. */
function moved(list: readonly Collection[], index: number, direction: -1 | 1): Collection[] {
  const target = index + direction
  if (target < 0 || target >= list.length) return [...list]
  const next = [...list]
  const [entry] = next.splice(index, 1)
  next.splice(target, 0, entry)
  return next
}

function sameOrder(a: readonly Collection[], b: readonly Collection[]): boolean {
  return a.length === b.length && a.every((collection, index) => collection.id === b[index].id)
}

/**
 * The order collections are evaluated in, which is what decides who claims a title that matches more
 * than one rule set — and therefore who can see it.
 *
 * Reordering is done with buttons rather than by dragging, deliberately. Dragging is the gesture
 * that works worst with a keyboard and a screen reader, and this list settles a permissions
 * question: a control that excludes those users would exclude them from a security decision, not
 * from a convenience. Both send the identical complete list, so a drag affordance could be layered
 * on later without changing the contract or removing these.
 *
 * A preview is required before saving, for the same reason it is on the rules themselves — with one
 * twist that makes it matter more here. Reordering only affects titles that match the rules of more
 * than one collection, and no screen shows which those are. Editing a condition at least shows you
 * what you typed; moving a row acts through overlaps nobody can see.
 */
export function CollectionOrderSection({ collections }: { collections: readonly Collection[] }) {
  const queryClient = useQueryClient()
  const inOrder = [...collections].sort((a, b) => a.rulePriority - b.rulePriority)
  const [draft, setDraft] = useState<Collection[] | null>(null)
  const [preview, setPreview] = useState<RulePreview | null>(null)
  const [confirming, setConfirming] = useState(false)
  /** Announced after a move: the visual jump is the feedback for everyone else. */
  const [announcement, setAnnouncement] = useState('')

  const order = draft ?? inOrder
  const dirty = !sameOrder(order, inOrder)
  const ids = order.map((collection) => collection.id)

  const runPreview = useMutation({
    mutationFn: () => catalogApi.previewCollectionRulePriority(ids),
    onSuccess: setPreview,
  })

  const save = useMutation({
    mutationFn: () => catalogApi.setCollectionRulePriority(ids),
    onSuccess: async () => {
      setConfirming(false)
      setDraft(null)
      setPreview(null)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['collections'] }),
        // Titles changed shelf, so what any account may browse changed with them.
        queryClient.invalidateQueries({ queryKey: ['works'] }),
      ])
    },
  })

  function move(index: number, direction: -1 | 1) {
    const next = moved(order, index, direction)
    if (sameOrder(next, order)) return
    setPreview(null)
    setDraft(next)
    setAnnouncement(
      `${order[index].name} moved to position ${index + direction + 1} of ${order.length}.`,
    )
  }

  return (
    <Card as="section" className="mb-4 space-y-3">
      <div>
        <h3 className="font-medium text-fg">Evaluation order</h3>
        <p className="mt-1 text-sm text-muted">
          A title lands on the first collection whose rules match it. This order is what settles a
          title that matches more than one — and with it, who can see that title.
        </p>
      </div>

      <ol className="divide-y divide-line rounded-lg border border-line">
        {order.map((collection, index) => (
          <li key={collection.id} className="flex items-center gap-3 px-3 py-2">
            <span className="w-6 shrink-0 text-sm text-faint">{index + 1}</span>
            <span className="min-w-0 flex-1 truncate text-sm text-fg">{collection.name}</span>
            <Badge tone={collection.accessMode === 'Restricted' ? 'warning' : 'success'}>
              {collection.accessMode === 'Restricted' ? 'Restricted' : 'Open'}
            </Badge>
            <Button
              type="button"
              size="sm"
              variant="ghost"
              disabled={index === 0 || save.isPending}
              onClick={() => move(index, -1)}
              icon={<ChevronRightIcon className="size-4 -rotate-90" />}
            >
              <span className="sr-only">Move {collection.name} earlier</span>
            </Button>
            <Button
              type="button"
              size="sm"
              variant="ghost"
              disabled={index === order.length - 1 || save.isPending}
              onClick={() => move(index, 1)}
              icon={<ChevronDownIcon className="size-4" />}
            >
              <span className="sr-only">Move {collection.name} later</span>
            </Button>
          </li>
        ))}
      </ol>

      {/* Mounted always, so the announcement is made rather than lost with the region that carries it. */}
      <p aria-live="polite" className="sr-only">
        {announcement}
      </p>

      {dirty && (
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            size="sm"
            variant="subtle"
            loading={runPreview.isPending}
            disabled={save.isPending}
            onClick={() => runPreview.mutate()}
          >
            Preview
          </Button>
          <Button
            type="button"
            size="sm"
            disabled={preview === null || save.isPending}
            onClick={() => setConfirming(true)}
          >
            Save order
          </Button>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            disabled={save.isPending}
            onClick={() => {
              setDraft(null)
              setPreview(null)
              setAnnouncement('Order reset.')
            }}
          >
            Reset
          </Button>
        </div>
      )}

      {dirty && preview === null && (
        <p className="text-xs text-faint">Preview this order to see which titles it would move.</p>
      )}

      {runPreview.isError && (
        <Alert tone="danger">{errorMessage(runPreview.error, 'This order could not be previewed.')}</Alert>
      )}

      {preview && <RulePreviewSummary preview={preview} collections={collections} />}

      {save.isError && (
        <Alert tone="danger">{errorMessage(save.error, 'The order could not be saved.')}</Alert>
      )}

      {save.isSuccess && (
        <Alert tone="success">
          Order saved. {save.data.moved} {save.data.moved === 1 ? 'title' : 'titles'} moved.
        </Alert>
      )}

      <Modal open={confirming} onClose={() => setConfirming(false)} title="Apply this evaluation order?">
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            {preview?.wouldMove ?? 0} {preview?.wouldMove === 1 ? 'title moves' : 'titles move'} to a
            different collection. A collection decides who may browse what is on it, so this changes
            who can see those titles.
          </p>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={save.isPending} onClick={() => setConfirming(false)}>
              Cancel
            </Button>
            <Button type="button" loading={save.isPending} onClick={() => save.mutate()}>
              Apply order
            </Button>
          </div>
        </div>
      </Modal>
    </Card>
  )
}
