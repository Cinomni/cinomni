import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { metadataApi } from '@/api/endpoints'
import type { ArtworkKind, MetadataArtwork } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { Alert } from '@/ui/Alert'
import { ErrorState } from '@/ui/ErrorState'
import { CheckIcon } from '@/ui/icons'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'
import { Spinner } from '@/ui/Spinner'

const TABS: { kind: ArtworkKind; label: string; aspect: string }[] = [
  { kind: 'Poster', label: 'Posters', aspect: 'aspect-[2/3]' },
  { kind: 'Backdrop', label: 'Backdrops', aspect: 'aspect-video' },
  { kind: 'Logo', label: 'Logos', aspect: 'aspect-video' },
]

export function ArtworkPicker({
  snapshotId,
  workId,
  open,
  onClose,
}: {
  snapshotId: string
  workId: string
  open: boolean
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<ArtworkKind>('Poster')

  const snapshotQuery = useQuery({
    queryKey: ['snapshot', snapshotId],
    queryFn: () => metadataApi.snapshot(snapshotId),
    enabled: open,
  })
  const snapshot = snapshotQuery.data

  const select = useMutation({
    mutationFn: (artworkId: string) => metadataApi.selectArtwork(snapshotId, artworkId),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['snapshot', snapshotId] }),
        queryClient.invalidateQueries({ queryKey: ['work', workId] }),
        queryClient.invalidateQueries({ queryKey: ['works'] }),
      ])
    },
  })

  const available = TABS.filter((tab) => snapshot?.artwork.some((a) => a.kind === tab.kind))
  const candidates = snapshot?.artwork.filter((a) => a.kind === kind) ?? []
  const activeTab = TABS.find((t) => t.kind === kind) ?? TABS[0]

  return (
    <Modal open={open} onClose={onClose} title="Choose artwork" className="max-w-3xl">
      {/* Selecting artwork writes to the snapshot the rest of the app reads. A refusal that showed
          nothing would look like the click missed, and the poster would stay as it was with no reason. */}
      {select.isError && (
        <Alert tone="danger" className="mb-4">
          {errorMessage(select.error, 'That artwork could not be selected.')}
        </Alert>
      )}

      {snapshotQuery.isPending ? (
        <LoadingBlock size="lg" label="Loading artwork candidates" />
      ) : snapshotQuery.isError ? (
        // "None were found" is a statement about the provider's answer. A snapshot we could not read
        // supports no such statement, and sending the operator to refresh metadata would not fix it.
        <ErrorState
          title="Artwork could not be loaded"
          message={errorMessage(snapshotQuery.error)}
          onRetry={() => void snapshotQuery.refetch()}
        />
      ) : snapshotQuery.data.artwork.length === 0 ? (
        <p className="py-8 text-center text-sm text-muted">
          No artwork candidates were found for this title. Refresh its metadata first.
        </p>
      ) : (
        <>
          <div className="mb-4 flex gap-1 rounded-control bg-elevated p-1">
            {available.map((tab) => (
              <button
                key={tab.kind}
                type="button"
                aria-pressed={kind === tab.kind}
                onClick={() => setKind(tab.kind)}
                className={cn(
                  'flex-1 rounded-md px-3 py-1.5 text-sm font-medium transition-colors',
                  kind === tab.kind ? 'bg-surface text-fg shadow-sm' : 'text-muted hover:text-fg',
                )}
              >
                {tab.label}
              </button>
            ))}
          </div>

          <div
            className={cn(
              'grid max-h-[55vh] gap-3 overflow-y-auto pr-1',
              kind === 'Poster' ? 'grid-cols-3 sm:grid-cols-4' : 'grid-cols-2 sm:grid-cols-3',
            )}
          >
            {candidates.map((art, index) => (
              <ArtworkOption
                key={art.id}
                artwork={art}
                label={artworkLabel(art, index, candidates.length)}
                aspect={activeTab.aspect}
                pending={select.isPending && select.variables === art.id}
                onSelect={() => !art.isSelected && select.mutate(art.id)}
              />
            ))}
          </div>
        </>
      )}
    </Modal>
  )
}

/**
 * What a screen reader announces for one candidate. The image itself is decorative (`alt=""`) — there is
 * no description of a poster to give — so the name says which one it is and what tells it apart:
 * its place in the list, its language and its size. Whether it is the chosen one is `aria-pressed`.
 */
function artworkLabel(artwork: MetadataArtwork, index: number, total: number): string {
  const parts = [`${artwork.kind} ${index + 1} of ${total}`]
  parts.push(artwork.language ? artwork.language.toUpperCase() : 'no language')
  if (artwork.width != null && artwork.height != null) parts.push(`${artwork.width} × ${artwork.height}`)
  return parts.join(', ')
}

function ArtworkOption({
  artwork,
  label,
  aspect,
  pending,
  onSelect,
}: {
  artwork: MetadataArtwork
  label: string
  aspect: string
  pending: boolean
  onSelect: () => void
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-label={label}
      aria-pressed={artwork.isSelected}
      className={cn(
        'group relative overflow-hidden rounded-control border-2 bg-elevated transition-colors',
        aspect,
        artwork.isSelected ? 'border-accent' : 'border-transparent hover:border-line',
      )}
    >
      <img src={artwork.thumbnailUrl ?? artwork.url} alt="" loading="lazy" decoding="async" className="size-full object-cover" />
      {artwork.language && (
        <span className="absolute bottom-1 left-1 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-medium uppercase text-fg">
          {artwork.language}
        </span>
      )}
      {(artwork.isSelected || pending) && (
        <span className="absolute right-1 top-1 grid size-6 place-items-center rounded-full bg-accent text-on-accent">
          {pending ? <Spinner className="size-3.5" /> : <CheckIcon className="size-4" />}
        </span>
      )}
    </button>
  )
}
