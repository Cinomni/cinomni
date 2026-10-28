import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { catalogApi } from '@/api/endpoints'
import type { Collection, CollectionKind, Work, WorkKind, WorkPageQuery } from '@/api/types'
import { useDebouncedValue } from '@/features/library/useLibraryPages'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Checkbox } from '@/ui/Checkbox'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'
import { TextField } from '@/ui/TextField'

/** A console search is a handful of titles, not a library grid. */
const PAGE_SIZE = 20

/**
 * Whether a shelf's kind can hold this title. The same rule the placement engine uses
 * (`Collection.Holds`); a warning here, not a gate. A move by hand is an override, and the server
 * accepts a shelf the rules would never claim.
 */
function shelfHolds(kind: CollectionKind, workKind: WorkKind): boolean {
  if (kind === 'Movies') return workKind === 'Movie'
  if (kind === 'Series') return workKind === 'Series'
  return true
}

function titleOf(work: Work): string {
  return work.year == null ? work.title : `${work.title} (${work.year})`
}

/**
 * Move one title by hand, or release the pin that move leaves behind.
 *
 * Both already existed as administrator routes. This is the screen that was missing: a search, the
 * shelf the title is on, and a confirmation that says who would newly be able to see it. Saving is
 * the confirmation — a move changes who may browse the title, so the dialog names the destination
 * before anything is written.
 */
export function CollectionPlacementSection({ collections }: { collections: readonly Collection[] }) {
  const [term, setTerm] = useState('')
  const [pinnedOnly, setPinnedOnly] = useState(false)
  const [moving, setMoving] = useState<Work | null>(null)
  const [releasing, setReleasing] = useState<Work | null>(null)
  const settled = useDebouncedValue(term.trim(), 250)

  const searching = settled.length > 0 || pinnedOnly
  const query: WorkPageQuery = {}
  if (settled) query.q = settled
  if (pinnedOnly) query.pinned = true

  const results = useQuery({
    queryKey: ['works', 'placement', query],
    queryFn: ({ signal }) => catalogApi.page(query, 0, PAGE_SIZE, signal),
    enabled: searching,
  })

  const byId = new Map(collections.map((collection) => [collection.id, collection]))

  return (
    <Card as="section" className="mt-4 space-y-3">
      <div>
        <h3 className="font-medium text-fg">Place a title</h3>
        <p className="mt-1 text-sm text-muted">
          Moving a title pins it on that shelf. Rules leave it there until you release the pin, and
          releasing the pin places it wherever the rules say at that moment — which can change who
          can see it.
        </p>
      </div>

      <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
        <div className="min-w-0 flex-1">
          <TextField
            label="Find a title"
            value={term}
            onChange={(event) => setTerm(event.target.value)}
            placeholder="Title"
            autoComplete="off"
          />
        </div>
        <Checkbox
          label="Pinned only"
          checked={pinnedOnly}
          onChange={(event) => setPinnedOnly(event.target.checked)}
          hint="Titles placed by hand."
        />
      </div>

      {!searching ? (
        <p className="text-sm text-faint">Search for a title, or show the ones already pinned.</p>
      ) : results.isPending ? (
        <LoadingBlock size="sm" label="Searching titles" />
      ) : results.isError ? (
        <div className="space-y-2">
          <Alert tone="danger">{errorMessage(results.error, 'Could not search titles.')}</Alert>
          <Button type="button" size="sm" variant="subtle" onClick={() => void results.refetch()}>
            Retry
          </Button>
        </div>
      ) : results.data.items.length === 0 ? (
        <p className="text-sm text-muted">No titles match.</p>
      ) : (
        <>
          <ul className="divide-y divide-line rounded-lg border border-line">
            {results.data.items.map((work) => {
              const shelf = work.collectionId ? byId.get(work.collectionId) : undefined
              return (
                <li key={work.id} className="flex flex-wrap items-center justify-between gap-3 px-3 py-2">
                  <div className="min-w-0">
                    <p className="truncate text-sm text-fg">{titleOf(work)}</p>
                    <div className="mt-1 flex flex-wrap items-center gap-2">
                      <Badge tone="neutral">{work.kind}</Badge>
                      <span className="text-xs text-faint">
                        {shelf ? `On ${shelf.name}` : 'On an unknown shelf'}
                      </span>
                      {shelf?.accessMode === 'Restricted' && <Badge tone="warning">Restricted</Badge>}
                      {work.collectionPinned && <Badge tone="neutral">Pinned</Badge>}
                    </div>
                  </div>
                  <div className="flex shrink-0 gap-2">
                    <Button type="button" size="sm" variant="subtle" onClick={() => setMoving(work)}>
                      Move<span className="sr-only"> {titleOf(work)}</span>
                    </Button>
                    {work.collectionPinned && (
                      <Button type="button" size="sm" variant="ghost" onClick={() => setReleasing(work)}>
                        Release pin<span className="sr-only"> on {titleOf(work)}</span>
                      </Button>
                    )}
                  </div>
                </li>
              )
            })}
          </ul>
          {results.data.total > results.data.items.length && (
            <p className="text-xs text-faint">
              Showing {results.data.items.length} of {results.data.total}. Narrow the search to see the rest.
            </p>
          )}
        </>
      )}

      <MoveDialog
        key={moving?.id ?? 'closed'}
        work={moving}
        collections={collections}
        current={moving?.collectionId ? byId.get(moving.collectionId) : undefined}
        onClose={() => setMoving(null)}
      />
      <ReleaseDialog work={releasing} onClose={() => setReleasing(null)} />
    </Card>
  )
}

function MoveDialog({
  work,
  collections,
  current,
  onClose,
}: {
  work: Work | null
  collections: readonly Collection[]
  current: Collection | undefined
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [destinationId, setDestinationId] = useState<string | null>(null)
  const destination =
    collections.find((collection) => collection.id === (destinationId ?? work?.collectionId ?? collections[0]?.id)) ??
    null

  const move = useMutation({
    mutationFn: () => {
      if (!work || !destination) throw new Error('Choose a collection.')
      return catalogApi.moveWork(work.id, destination.id)
    },
    onSuccess: async () => {
      setDestinationId(null)
      onClose()
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['works'] }),
        queryClient.invalidateQueries({ queryKey: ['collections'] }),
        work ? queryClient.invalidateQueries({ queryKey: ['work', work.id] }) : Promise.resolve(),
      ])
    },
  })

  const leavingRestricted = current?.accessMode === 'Restricted' && destination?.accessMode === 'Open'
  const enteringRestricted = current?.accessMode === 'Open' && destination?.accessMode === 'Restricted'
  const wrongKind = work != null && destination != null && !shelfHolds(destination.kind, work.kind)
  const sameShelf = work != null && destination != null && work.collectionId === destination.id

  return (
    <Modal open={work != null} onClose={onClose} title={work ? `Move ${titleOf(work)}?` : 'Move a title'}>
      {work && (
        <div className="flex flex-col gap-4">
          <Select
            label="Collection"
            value={destination?.id ?? ''}
            onChange={(event) => {
              move.reset()
              setDestinationId(event.target.value)
            }}
          >
            {collections.map((collection) => (
              <option key={collection.id} value={collection.id}>
                {collection.name}
                {collection.accessMode === 'Restricted' ? ' (restricted)' : ''}
              </option>
            ))}
          </Select>

          <p className="text-sm text-muted">
            {sameShelf
              ? `It is already on ${destination?.name}. Confirming pins it there, so later rules leave it alone.`
              : `Moving it to ${destination?.name ?? 'that shelf'} pins it there. Rules leave it alone until the pin is released.`}
          </p>

          {leavingRestricted && (
            <Alert tone="warning">
              This title would leave a restricted collection for an open one. More people would be able to see it.
            </Alert>
          )}
          {enteringRestricted && (
            <Alert tone="warning">
              This title would leave an open collection for a restricted one. Only the accounts granted that shelf would see it.
            </Alert>
          )}
          {wrongKind && destination && (
            <Alert tone="warning">
              {destination.name} is for {destination.kind === 'Movies' ? 'movies' : 'series'} only. A{' '}
              {work.kind === 'Movie' ? 'movie' : 'series'} can still be pinned here; rules will never place one here.
            </Alert>
          )}
          {move.isError && <Alert tone="danger">{errorMessage(move.error, 'The title could not be moved.')}</Alert>}

          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={move.isPending} onClick={onClose}>
              Cancel
            </Button>
            <Button type="button" loading={move.isPending} disabled={destination == null} onClick={() => move.mutate()}>
              Move and pin
            </Button>
          </div>
        </div>
      )}
    </Modal>
  )
}

function ReleaseDialog({ work, onClose }: { work: Work | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const release = useMutation({
    mutationFn: () => {
      if (!work) throw new Error('No title.')
      return catalogApi.unpinWork(work.id)
    },
    onSuccess: async () => {
      onClose()
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['works'] }),
        queryClient.invalidateQueries({ queryKey: ['collections'] }),
        work ? queryClient.invalidateQueries({ queryKey: ['work', work.id] }) : Promise.resolve(),
      ])
    },
  })

  return (
    <Modal open={work != null} onClose={onClose} title={work ? `Release the pin on ${titleOf(work)}?` : 'Release a pin'}>
      {work && (
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            The rules place it wherever they say now. That can move it to another shelf, and a shelf
            decides who can see it.
          </p>
          {release.isError && (
            <Alert tone="danger">{errorMessage(release.error, 'The pin could not be released.')}</Alert>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={release.isPending} onClick={onClose}>
              Cancel
            </Button>
            <Button type="button" loading={release.isPending} onClick={() => release.mutate()}>
              Release pin
            </Button>
          </div>
        </div>
      )}
    </Modal>
  )
}
