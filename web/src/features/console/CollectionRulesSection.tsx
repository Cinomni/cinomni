import { useId, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { catalogApi } from '@/api/endpoints'
import type {
  Collection,
  CollectionRule,
  CollectionRuleCondition,
  CollectionRuleField,
  CollectionRuleOperator,
  RulePreview,
} from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'
import { TextField } from '@/ui/TextField'
import { RulePreviewSummary, countNewlyExposed } from './RulePreviewSummary'
import {
  OPERATOR_LABEL,
  RULE_FIELDS,
  RULE_FIELD_ORDER,
  acceptsMultipleValues,
  defaultOperatorFor,
  ruleProblem,
} from './collectionRules'

/** Values are one per line in the editor and a list on the wire. */
function splitValues(raw: string): string[] {
  return raw
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

function isField(value: string): value is CollectionRuleField {
  return value in RULE_FIELDS
}

function isOperator(field: CollectionRuleField, value: string): value is CollectionRuleOperator {
  return (RULE_FIELDS[field].operators as readonly string[]).includes(value)
}

/**
 * The multi-value editor: one alternative per line.
 *
 * It keeps the raw text rather than re-deriving it from the parsed values, because the two are not
 * the same string. Splitting and rejoining on every keystroke discards the newline the moment it is
 * typed — a second line could never be started — and it would also swallow the blank line someone
 * leaves while thinking. The parsed list still goes up on every change; only the display is raw.
 */
function ValueLines({
  values,
  hint,
  disabled,
  onChange,
}: {
  values: string[]
  hint?: string
  disabled: boolean
  onChange: (values: string[]) => void
}) {
  const fieldId = useId()
  const [raw, setRaw] = useState(() => values.join('\n'))

  return (
    <div className="flex min-w-48 flex-1 flex-col gap-1.5">
      <label htmlFor={fieldId} className="text-sm font-medium text-muted">
        Values, one per line
      </label>
      <textarea
        id={fieldId}
        rows={3}
        className="rounded-lg border border-line bg-surface px-3 py-2 font-mono text-sm text-fg focus:border-accent focus:outline-none"
        value={raw}
        disabled={disabled}
        onChange={(event) => {
          setRaw(event.target.value)
          onChange(splitValues(event.target.value))
        }}
      />
      {hint && <p className="text-xs text-faint">{hint}</p>}
    </div>
  )
}

function ConditionEditor({
  condition,
  disabled,
  onChange,
  onRemove,
}: {
  condition: CollectionRuleCondition
  disabled: boolean
  onChange: (condition: CollectionRuleCondition) => void
  onRemove: () => void
}) {
  const spec = RULE_FIELDS[condition.field]
  const multi = acceptsMultipleValues(condition.field, condition.operator)

  return (
    <div className="flex flex-wrap items-start gap-2 rounded-lg border border-line bg-elevated p-3">
      <Select
        label="Field"
        className="w-44 text-sm"
        value={condition.field}
        disabled={disabled}
        onChange={(event) => {
          if (!isField(event.target.value)) return
          const field = event.target.value
          // The operator may not exist on the new field, and the values may not be legal for it.
          onChange({ field, operator: defaultOperatorFor(field), values: [] })
        }}
      >
        {RULE_FIELD_ORDER.map((field) => (
          <option key={field} value={field}>
            {RULE_FIELDS[field].label}
          </option>
        ))}
      </Select>

      <Select
        label="Operator"
        className="w-40 text-sm"
        value={condition.operator}
        disabled={disabled}
        onChange={(event) => {
          if (!isOperator(condition.field, event.target.value)) return
          const operator = event.target.value
          // Dropping to a single-value operator keeps the first value rather than silently sending
          // a list the API would refuse.
          const values = acceptsMultipleValues(condition.field, operator)
            ? condition.values
            : condition.values.slice(0, 1)
          onChange({ ...condition, operator, values })
        }}
      >
        {spec.operators.map((operator) => (
          <option key={operator} value={operator}>
            {OPERATOR_LABEL[operator]}
          </option>
        ))}
      </Select>

      {spec.choices ? (
        <Select
          label="Value"
          className="w-40 text-sm"
          value={condition.values[0] ?? ''}
          disabled={disabled}
          onChange={(event) => onChange({ ...condition, values: [event.target.value] })}
        >
          <option value="">Choose…</option>
          {spec.choices.map((choice) => (
            <option key={choice} value={choice}>
              {choice}
            </option>
          ))}
        </Select>
      ) : multi ? (
        <ValueLines
          key={condition.field + condition.operator}
          values={condition.values}
          hint={spec.hint}
          disabled={disabled}
          onChange={(values) => onChange({ ...condition, values })}
        />
      ) : (
        <TextField
          label="Value"
          className="w-40"
          inputMode={spec.value === 'integer' ? 'numeric' : undefined}
          value={condition.values[0] ?? ''}
          disabled={disabled}
          onChange={(event) => onChange({ ...condition, values: [event.target.value] })}
          hint={spec.hint}
        />
      )}

      <Button
        type="button"
        size="sm"
        variant="ghost"
        disabled={disabled}
        className="mt-6"
        onClick={onRemove}
      >
        Remove
        <span className="sr-only"> this condition</span>
      </Button>
    </div>
  )
}

/**
 * The rule set that decides which titles land on one collection.
 *
 * Saving here is not an edit to a form: it can move hundreds of titles between shelves, and a shelf
 * is what decides who may see what is on it. So a preview is not a convenience offered alongside the
 * save — it is required before it, and the save says what it is about to do before it does it.
 */
export function CollectionRulesSection({
  collection,
  collections,
}: {
  collection: Collection
  collections: readonly Collection[]
}) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<CollectionRule[] | null>(null)
  const [preview, setPreview] = useState<RulePreview | null>(null)
  const [confirming, setConfirming] = useState(false)

  const rules = useQuery({
    queryKey: ['collection-rules', collection.id],
    queryFn: () => catalogApi.collectionRules(collection.id),
  })

  const editing = draft ?? rules.data ?? []
  // One rule per collection is what this screen edits; a second is expressed as another condition.
  const rule = editing[0] ?? null
  const conditions = rule?.conditions ?? []
  const problem = ruleProblem(conditions)

  const runPreview = useMutation({
    mutationFn: () => catalogApi.previewCollectionRules(collection.id, editing),
    onSuccess: setPreview,
  })

  const save = useMutation({
    mutationFn: () => catalogApi.setCollectionRules(collection.id, editing),
    onSuccess: async () => {
      setConfirming(false)
      setDraft(null)
      setPreview(null)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['collection-rules', collection.id] }),
        queryClient.invalidateQueries({ queryKey: ['collections'] }),
        // A title that changed shelf changed who may browse it, so every catalog list is stale.
        queryClient.invalidateQueries({ queryKey: ['works'] }),
      ])
    },
  })

  function updateConditions(next: CollectionRuleCondition[]) {
    setPreview(null)
    setDraft([
      {
        id: rule?.id ?? '',
        collectionId: collection.id,
        name: rule?.name ?? collection.name,
        conditions: next,
      },
    ])
  }

  if (rules.isPending) {
    return <LoadingBlock size="sm" label="Loading rules" />
  }
  if (rules.isError) {
    return (
      <ErrorState
        title="Rules could not be loaded"
        message={errorMessage(rules.error)}
        onRetry={() => void rules.refetch()}
      />
    )
  }

  return (
    <div className="mt-3 space-y-3 border-t border-line pt-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-medium text-fg">Rules</h3>
        <span className="text-xs text-faint">Evaluated in order {collection.rulePriority}</span>
      </div>

      <p className="text-sm text-muted">
        A title lives on exactly one shelf, so these rules do not tag it — they decide where it goes,
        and with it who can see it. The first collection whose rules match claims the title.
      </p>

      {conditions.length === 0 ? (
        <p className="text-sm text-faint">No rules. Titles reach this collection only by hand.</p>
      ) : (
        <div className="space-y-2">
          {conditions.map((condition, index) => (
            <ConditionEditor
              key={index}
              condition={condition}
              disabled={save.isPending}
              onChange={(next) =>
                updateConditions(conditions.map((existing, at) => (at === index ? next : existing)))
              }
              onRemove={() => updateConditions(conditions.filter((_, at) => at !== index))}
            />
          ))}
          <p className="text-xs text-faint">Every condition has to match. Values within one are alternatives.</p>
        </div>
      )}

      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          size="sm"
          variant="subtle"
          disabled={save.isPending}
          onClick={() => updateConditions([...conditions, { field: 'Kind', operator: 'Is', values: [] }])}
        >
          Add condition
        </Button>
        <Button
          type="button"
          size="sm"
          variant="subtle"
          loading={runPreview.isPending}
          disabled={problem !== null || save.isPending}
          onClick={() => runPreview.mutate()}
        >
          Preview
        </Button>
        <Button
          type="button"
          size="sm"
          // Deliberately gated on a preview: the operator sees what this does before it does it.
          disabled={problem !== null || preview === null || save.isPending}
          onClick={() => setConfirming(true)}
        >
          Save rules
        </Button>
      </div>

      {problem && (
        <p role="alert" className="text-sm text-danger">
          {problem}
        </p>
      )}

      {preview === null && problem === null && conditions.length > 0 && (
        <p className="text-xs text-faint">Preview these rules to see what they would move before saving.</p>
      )}

      {runPreview.isError && (
        <Alert tone="danger">{errorMessage(runPreview.error, 'The rules could not be previewed.')}</Alert>
      )}

      {preview && <RulePreviewSummary preview={preview} collections={collections} />}

      {save.isError && (
        <Alert tone="danger">{errorMessage(save.error, 'The rules could not be saved.')}</Alert>
      )}

      {save.isSuccess && (
        <Alert tone="success">
          {save.data.moved} {save.data.moved === 1 ? 'title' : 'titles'} moved.
        </Alert>
      )}

      <Modal
        open={confirming}
        onClose={() => setConfirming(false)}
        title={`Apply these rules to ${collection.name}?`}
      >
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            {preview?.wouldMove ?? 0} {preview?.wouldMove === 1 ? 'title moves' : 'titles move'} onto{' '}
            <span className="font-medium text-fg">{collection.name}</span>, which is{' '}
            {collection.accessMode === 'Restricted'
              ? 'restricted — only the accounts you granted will see them.'
              : 'open — every account in the household will see them.'}
          </p>
          {preview && countNewlyExposed(preview, collections) > 0 && (
            <Alert tone="warning">
              Some of them are on a restricted shelf today. This is what makes them visible to
              everyone, and nothing else in this flow will say so.
            </Alert>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={save.isPending} onClick={() => setConfirming(false)}>
              Cancel
            </Button>
            <Button type="button" loading={save.isPending} onClick={() => save.mutate()}>
              Apply rules
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  )
}
