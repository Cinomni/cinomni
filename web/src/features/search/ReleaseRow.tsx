import { useId, useState } from 'react'
import type { EvaluatedCandidate } from '@/api/types'
import { formatBytes, formatRelative } from '@/lib/format'
import { verdictLabel, verdictTone } from '@/lib/status'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { ChevronDownIcon, ChevronRightIcon, DownloadIcon } from '@/ui/icons'
import { TextArea } from '@/ui/TextArea'
import { ReleaseReasons } from './ReleaseReasons'
import { ReleaseSource } from './ReleaseSource'

/**
 * One candidate of an interactive search: what the indexer offered, what the profile made of it, and
 * — on demand — every reason behind that verdict.
 *
 * A rejected release is still grabbable, because overriding the profile is the point of searching by
 * hand. It just has to be deliberate: the override asks a second time, and says what it is overriding.
 */
export function ReleaseRow({
  candidate,
  onGrab,
  grabbing,
  grabbed,
  onBlock,
  blocking,
  onUnblock,
  unblocking,
}: {
  candidate: EvaluatedCandidate
  onGrab: () => void
  grabbing: boolean
  grabbed: boolean
  onBlock: (reason: string) => void
  blocking: boolean
  onUnblock: () => void
  unblocking: boolean
}) {
  const panelId = useId()
  const [expanded, setExpanded] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [blockingOpen, setBlockingOpen] = useState(false)
  const [blockReason, setBlockReason] = useState('')
  const [blockError, setBlockError] = useState<string | null>(null)

  // Whether a grab needs asking twice is the server's call: a release rejected only for a block that
  // has since been lifted is still badged as rejected, but taking it overrides nothing.
  const overrides = candidate.grabOverridesVerdict
  const failedRule = candidate.reasons.find((reason) => reason.outcome === 'Fail')
  const blocked = candidate.blocked

  return (
    <Card as="li" padding="sm">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <p className="break-words font-mono text-xs text-fg">{candidate.releaseTitle}</p>

          <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
            <Badge tone={verdictTone[candidate.verdict]}>{verdictLabel[candidate.verdict]}</Badge>
            {candidate.isRecommended && <Badge tone="accent">Best match</Badge>}
            {blocked && <Badge tone="warning">Blocked</Badge>}
            {candidate.episodeCoverage > 1 && (
              <Badge tone="info">{candidate.episodeCoverage} episodes</Badge>
            )}
            {candidate.customFormatScore !== 0 && (
              <Badge tone="neutral">Score {candidate.customFormatScore}</Badge>
            )}
          </div>

          <ReleaseSource
            className="mt-1.5"
            indexerName={candidate.indexerName}
            seeders={candidate.seeders}
            leechers={candidate.leechers}
          />
          <p className="mt-0.5 text-xs text-faint">
            {formatBytes(candidate.sizeBytes)}
            {candidate.publishedAt && <> · {formatRelative(candidate.publishedAt)}</>}
          </p>
        </div>

        <div className="flex shrink-0 items-center gap-1">
          <Button
            size="sm"
            variant="ghost"
            aria-expanded={expanded}
            aria-controls={panelId}
            icon={
              expanded ? <ChevronDownIcon className="size-4" /> : <ChevronRightIcon className="size-4" />
            }
            onClick={() => setExpanded((open) => !open)}
          >
            Why
          </Button>

          {blocked ? (
            <Button size="sm" variant="subtle" loading={unblocking} onClick={onUnblock}>
              Unblock
            </Button>
          ) : (
            <Button size="sm" variant="ghost" onClick={() => setBlockingOpen(true)}>
              Block
            </Button>
          )}

          {grabbed ? (
            <Badge tone="success">Queued</Badge>
          ) : (
            <Button
              size="sm"
              variant={overrides ? 'subtle' : 'primary'}
              loading={grabbing}
              disabled={blocked}
              icon={<DownloadIcon className="size-4" />}
              onClick={() => (overrides ? setConfirming(true) : onGrab())}
            >
              Grab
            </Button>
          )}
        </div>
      </div>

      {blocked && candidate.blockReason && (
        <p className="mt-2 text-sm text-muted">Blocked: {candidate.blockReason}</p>
      )}

      {blockingOpen && !blocked && (
        <form
          className="mt-3 space-y-2 rounded-control border border-line p-3"
          onSubmit={(event) => {
            event.preventDefault()
            const reason = blockReason.trim()
            if (reason.length === 0 || reason.length > 500) {
              setBlockError('A block needs a reason, of at most 500 characters.')
              return
            }
            setBlockError(null)
            onBlock(reason)
          }}
        >
          <TextArea
            label={`Why block ${candidate.releaseTitle}?`}
            rows={3}
            value={blockReason}
            onChange={(event) => setBlockReason(event.target.value)}
            error={blockError ?? undefined}
          />
          <div className="flex gap-2">
            <Button size="sm" type="submit" variant="danger" loading={blocking}>
              Block release
            </Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setBlockingOpen(false)}>
              Cancel
            </Button>
          </div>
        </form>
      )}

      {confirming && !grabbed && !blocked && (
        <div className="mt-3 rounded-control border border-warning/30 bg-warning/10 p-3">
          <p className="text-sm text-fg">
            The profile rejected this release
            {failedRule && <> ({failedRule.rule.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase()})</>}.
            Download it anyway?
          </p>
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

      <div id={panelId} hidden={!expanded} className="mt-3 border-t border-line pt-3">
        {expanded && <ReleaseReasons reasons={candidate.reasons} />}
      </div>
    </Card>
  )
}
