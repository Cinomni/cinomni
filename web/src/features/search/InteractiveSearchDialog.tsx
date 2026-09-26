import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { decisionApi } from '@/api/endpoints'
import type { EvaluatedCandidate, InteractiveSearchResult, ReleaseEvaluation } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { verdictLabel, verdictTone } from '@/lib/status'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { EmptyState } from '@/ui/EmptyState'
import { DownloadIcon, SearchIcon } from '@/ui/icons'
import { Modal } from '@/ui/Modal'
import { Spinner } from '@/ui/Spinner'
import { ReleaseReasons } from './ReleaseReasons'
import { ReleaseRow } from './ReleaseRow'
import { ReleaseSource } from './ReleaseSource'

/**
 * Search one monitored target by hand and pick a release from the results.
 *
 * It opens on what the platform already decided — the reasons it persisted the last time it searched
 * this target — because that usually answers the question that brought the user here ("why is this
 * still missing?") without asking an indexer anything. Searching again is one deliberate click, and
 * what comes back is judged by exactly the same profile the automatic pipeline uses; the only thing
 * this surface adds is that a person may overrule the verdict.
 */
export function InteractiveSearchDialog({
  targetId,
  title,
  open,
  onClose,
}: {
  targetId: string
  title: string
  open: boolean
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [result, setResult] = useState<InteractiveSearchResult | null>(null)
  const [grabbed, setGrabbed] = useState<ReadonlySet<string>>(new Set())
  const [blockOverrides, setBlockOverrides] = useState<
    Record<string, { blocked: boolean; blockId: string | null; blockReason: string | null }>
  >({})

  const history = useQuery({
    queryKey: ['evaluations', targetId],
    queryFn: () => decisionApi.evaluations(targetId),
    enabled: open && !result,
  })

  const search = useMutation({
    mutationFn: () => decisionApi.search(targetId),
    onSuccess: (found) => {
      setBlockOverrides({})
      setResult(found)
    },
  })

  const block = useMutation({
    mutationFn: (input: { evaluationId: string; reason: string }) =>
      decisionApi.block(input.evaluationId, input.reason),
    onSuccess: (created, input) => {
      setBlockOverrides((current) => ({
        ...current,
        [input.evaluationId]: { blocked: true, blockId: created.id, blockReason: created.reason },
      }))
      void queryClient.invalidateQueries({ queryKey: ['decision', 'blocks'] })
    },
  })

  const unblock = useMutation({
    mutationFn: (input: { evaluationId: string; blockId: string }) => decisionApi.unblock(input.blockId),
    onSuccess: (_void, input) => {
      setBlockOverrides((current) => ({
        ...current,
        [input.evaluationId]: { blocked: false, blockId: null, blockReason: null },
      }))
      void queryClient.invalidateQueries({ queryKey: ['decision', 'blocks'] })
    },
  })

  const grab = useMutation({
    mutationFn: (evaluationId: string) => decisionApi.grab(evaluationId),
    onSuccess: (selection) => {
      setGrabbed((previous) => new Set(previous).add(selection.evaluationId))
      // The live stream will say the same thing; asking now is what makes the click feel answered.
      void queryClient.invalidateQueries({ queryKey: ['intents'] })
      void queryClient.invalidateQueries({ queryKey: ['downloads'] })
    },
  })

  function close() {
    setResult(null)
    setGrabbed(new Set())
    setBlockOverrides({})
    search.reset()
    grab.reset()
    block.reset()
    unblock.reset()
    onClose()
  }

  return (
    <Modal open={open} onClose={close} title={`Search — ${title}`} className="max-w-3xl">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted">
          {result
            ? `${result.candidates.length} release${result.candidates.length === 1 ? '' : 's'} for “${result.term}”`
            : 'Ask every enabled indexer now and choose a release yourself.'}
        </p>
        <Button
          icon={<SearchIcon className="size-4" />}
          loading={search.isPending}
          onClick={() => search.mutate()}
        >
          {result ? 'Search again' : 'Search indexers'}
        </Button>
      </div>

      {search.isError && (
        <Alert tone="danger" className="mt-3">
          {errorMessage(search.error, 'The search could not be run.')}
        </Alert>
      )}
      {grab.isError && (
        <Alert tone="danger" className="mt-3">
          {errorMessage(grab.error, 'That release could not be queued.')}
        </Alert>
      )}
      {block.isError && (
        <Alert tone="danger" className="mt-3">
          {errorMessage(block.error, 'That release could not be blocked.')}
        </Alert>
      )}
      {unblock.isError && (
        <Alert tone="danger" className="mt-3">
          {errorMessage(unblock.error, 'That release could not be unblocked. It is still blocked.')}
        </Alert>
      )}

      <div className="mt-4">
        {search.isPending ? (
          <div className="grid place-items-center py-12">
            <Spinner className="size-6 text-accent" />
          </div>
        ) : result ? (
          <Results
            candidates={result.candidates}
            grabbed={grabbed}
            grabbingId={grab.isPending ? grab.variables : null}
            onGrab={(evaluationId) => grab.mutate(evaluationId)}
            blockOverrides={blockOverrides}
            blockingId={block.isPending ? block.variables.evaluationId : null}
            unblockingId={unblock.isPending ? unblock.variables.evaluationId : null}
            onBlock={(evaluationId, reason) => block.mutate({ evaluationId, reason })}
            onUnblock={(evaluationId, blockId) => unblock.mutate({ evaluationId, blockId })}
          />
        ) : (
          <History
            evaluations={history.data}
            isPending={history.isPending && history.isFetching}
            grabbed={grabbed}
            grabbingId={grab.isPending ? grab.variables : null}
            onGrab={(evaluationId) => grab.mutate(evaluationId)}
          />
        )}
      </div>
    </Modal>
  )
}

function Results({
  candidates,
  grabbed,
  grabbingId,
  onGrab,
  blockOverrides,
  blockingId,
  unblockingId,
  onBlock,
  onUnblock,
}: {
  candidates: EvaluatedCandidate[]
  grabbed: ReadonlySet<string>
  grabbingId: string | null
  onGrab: (evaluationId: string) => void
  blockOverrides: Record<string, { blocked: boolean; blockId: string | null; blockReason: string | null }>
  blockingId: string | null
  unblockingId: string | null
  onBlock: (evaluationId: string, reason: string) => void
  onUnblock: (evaluationId: string, blockId: string) => void
}) {
  if (candidates.length === 0) {
    return (
      <EmptyState
        icon={<SearchIcon className="size-8" />}
        title="No releases found"
        description="No enabled indexer returned anything for this search. Check the indexer settings, or try again later."
      />
    )
  }

  return (
    <ul className="max-h-[55vh] space-y-2 overflow-y-auto">
      {candidates.map((candidate) => (
        <ReleaseRow
          key={candidate.evaluationId}
          candidate={applyBlock(candidate, blockOverrides[candidate.evaluationId])}
          grabbed={grabbed.has(candidate.evaluationId)}
          grabbing={grabbingId === candidate.evaluationId}
          onGrab={() => onGrab(candidate.evaluationId)}
          blocking={blockingId === candidate.evaluationId}
          unblocking={unblockingId === candidate.evaluationId}
          onBlock={(reason) => onBlock(candidate.evaluationId, reason)}
          onUnblock={() => {
            const blockId = applyBlock(candidate, blockOverrides[candidate.evaluationId]).blockId
            if (blockId) onUnblock(candidate.evaluationId, blockId)
          }}
        />
      ))}
    </ul>
  )
}

function applyBlock(
  candidate: EvaluatedCandidate,
  override: { blocked: boolean; blockId: string | null; blockReason: string | null } | undefined,
): EvaluatedCandidate {
  if (!override) return candidate
  return { ...candidate, ...override }
}

/**
 * What the platform decided the last time it searched this target, newest first — and each of those
 * releases can be taken from here. The server still holds the search that found it; when it no longer
 * does, the grab is refused with "search again", which the dialog shows above the list.
 */
function History({
  evaluations,
  isPending,
  grabbed,
  grabbingId,
  onGrab,
}: {
  evaluations?: ReleaseEvaluation[]
  isPending: boolean
  grabbed: ReadonlySet<string>
  grabbingId: string | null
  onGrab: (evaluationId: string) => void
}) {
  if (isPending) {
    return (
      <div className="grid place-items-center py-12">
        <Spinner className="size-6 text-accent" />
      </div>
    )
  }

  if (!evaluations || evaluations.length === 0) {
    return (
      <EmptyState
        icon={<SearchIcon className="size-8" />}
        title="Nothing evaluated yet"
        description="Once a search runs for this title, every release it judged shows up here with the reasons."
      />
    )
  }

  return (
    <>
      <h3 className="text-label uppercase text-faint">Previously evaluated</h3>
      <p className="mb-3 mt-1 text-meta text-muted">
        From the last automatic search. Grab one, or search again for fresh results.
      </p>
      <ul className="max-h-[45vh] space-y-2 overflow-y-auto">
        {evaluations.slice(0, 20).map((evaluation) => (
          <HistoryRow
            key={evaluation.id}
            evaluation={evaluation}
            grabbed={grabbed.has(evaluation.id)}
            grabbing={grabbingId === evaluation.id}
            onGrab={() => onGrab(evaluation.id)}
          />
        ))}
      </ul>
    </>
  )
}


function HistoryRow({
  evaluation,
  grabbed,
  grabbing,
  onGrab,
}: {
  evaluation: ReleaseEvaluation
  grabbed: boolean
  grabbing: boolean
  onGrab: () => void
}) {
  const [confirming, setConfirming] = useState(false)
  // A past evaluation does not carry the server's override flag, so anything the profile did not
  // accept asks twice. The server still makes the real call and records the override itself.
  const rejected = evaluation.verdict !== 'Accepted'

  return (
    <Card as="li" padding="sm">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <p className="break-words font-mono text-xs text-fg">{evaluation.releaseTitle}</p>
          <div className="mt-1.5">
            <Badge tone={verdictTone[evaluation.verdict]}>{verdictLabel[evaluation.verdict]}</Badge>
          </div>
          {evaluation.indexerName && (
            <ReleaseSource
              className="mt-1.5"
              indexerName={evaluation.indexerName}
              seeders={evaluation.seeders}
              leechers={evaluation.leechers}
            />
          )}
        </div>
        {grabbed ? (
          <Badge tone="success">Queued</Badge>
        ) : (
          <Button
            size="sm"
            variant={rejected ? 'subtle' : 'primary'}
            loading={grabbing}
            icon={<DownloadIcon className="size-4" />}
            onClick={() => (rejected ? setConfirming(true) : onGrab())}
          >
            Grab
            <span className="sr-only"> {evaluation.releaseTitle}</span>
          </Button>
        )}
      </div>

      {confirming && !grabbed && (
        <div className="mt-3 rounded-control bg-warning/8 p-3">
          <p className="text-meta text-fg">The profile did not accept this release. Download it anyway?</p>
          <div className="mt-2 flex gap-2">
            <Button
              size="sm"
              loading={grabbing}
              onClick={() => {
                setConfirming(false)
                onGrab()
              }}
            >
              Download anyway
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setConfirming(false)}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      <div className="mt-2 border-t border-line-soft pt-2">
        <ReleaseReasons reasons={evaluation.reasons} />
      </div>
    </Card>
  )
}
