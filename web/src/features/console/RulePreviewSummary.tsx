import type { Collection, RulePreview, RulePreviewWork } from '@/api/types'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'

/**
 * The titles a preview would move from a restricted shelf onto an open one.
 *
 * This is the direction that leaks, and it is the only one worth interrupting someone for. Moving a
 * title INTO a restricted collection narrows its audience — recoverable, and rarely a surprise to
 * whoever asked for it. Moving one OUT shows it to the whole household, and nothing else in either
 * flow says so.
 *
 * Two cases reach here and only one of them looks like a move. The obvious one is a rule claiming a
 * title away from a restricted shelf. The other is a title that no rule claims any more: the server
 * returns it to the default collection, which is open, so it is exposed by ceasing to match rather
 * than by being moved anywhere. It is indistinguishable from the first here, which is the point —
 * an operator has no chance of anticipating the second, and this counts it without being told.
 *
 * Every judgement is the server's: which titles move, and where to. This reads its answer against
 * the access modes the collections list already reports, and decides nothing about who may see what.
 */
export function countNewlyExposed(preview: RulePreview, collections: readonly Collection[]): number {
  const modeOf = new Map(collections.map((collection) => [collection.id, collection.accessMode]))
  return preview.works.filter(
    (work) =>
      !work.pinned &&
      modeOf.get(work.currentCollectionId) === 'Restricted' &&
      modeOf.get(work.targetCollectionId) === 'Open',
  ).length
}

function WorkRow({ work }: { work: RulePreviewWork }) {
  const moving = work.currentCollectionId !== work.targetCollectionId

  return (
    <li className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
      <span className="min-w-0 truncate text-fg">
        {work.title}
        {work.year != null && <span className="text-faint"> ({work.year})</span>}
      </span>
      <span className="flex shrink-0 items-center gap-2 text-xs text-faint">
        {moving ? (
          <>
            {work.currentCollectionName} <span aria-hidden="true">→</span>
            <span className="sr-only">to</span>
            <span className="text-fg">{work.targetCollectionName}</span>
          </>
        ) : (
          <>stays on {work.currentCollectionName}</>
        )}
        {work.pinned && <Badge tone="neutral">Pinned</Badge>}
      </span>
    </li>
  )
}

/**
 * What a rule set or an evaluation order would do, before it does it. Shared by both previews
 * because the API answers both with the same shape, deliberately.
 */
export function RulePreviewSummary({
  preview,
  collections,
}: {
  preview: RulePreview
  collections: readonly Collection[]
}) {
  const newlyExposed = countNewlyExposed(preview, collections)

  return (
    <div className="space-y-2">
      <dl className="grid grid-cols-3 gap-2 text-sm">
        <div>
          <dt className="text-faint">Match</dt>
          <dd className="text-fg">{preview.matched}</dd>
        </div>
        <div>
          <dt className="text-faint">Would move</dt>
          <dd className="font-medium text-fg">{preview.wouldMove}</dd>
        </div>
        <div>
          <dt className="text-faint">Pinned, left alone</dt>
          <dd className="text-fg">{preview.pinnedSkipped}</dd>
        </div>
      </dl>
      <p className="text-xs text-faint">
        A title that matches does not always move: one placed by hand stays where it was put, and one
        already on the shelf that claims it has nowhere to go.
      </p>

      {newlyExposed > 0 && (
        <Alert tone="warning" title="This widens who can see these titles">
          {newlyExposed} of the titles that would move {newlyExposed === 1 ? 'is' : 'are'} on a
          restricted shelf today and would land on an open one, visible to every account in the
          household. That includes any title no rule claims any more, which returns to the default
          collection.
        </Alert>
      )}

      {preview.works.length > 0 && (
        <ul className="max-h-56 divide-y divide-line overflow-y-auto rounded-lg border border-line">
          {preview.works.map((work) => (
            <WorkRow key={work.id} work={work} />
          ))}
        </ul>
      )}
      {preview.works.length < preview.wouldMove + preview.pinnedSkipped && (
        <p className="text-xs text-faint">Showing the first page of affected titles, not all of them.</p>
      )}
    </div>
  )
}
