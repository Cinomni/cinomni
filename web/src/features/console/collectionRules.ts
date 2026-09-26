import type { CollectionRuleCondition, CollectionRuleField, CollectionRuleOperator } from '@/api/types'

/**
 * The client's mirror of the grammar the API accepts. Its whole job is to stop this screen offering
 * a combination the server will refuse: the backend answers `catalog.rule.operator_not_allowed` and
 * `catalog.rule.too_many_values` for exactly the shapes this table forbids, and a builder that let
 * an operator assemble one would be inviting a rejection it could have prevented.
 *
 * It is a mirror, not the authority. The server validates again and its answer wins; this only means
 * a valid-looking rule is not composed in the first place.
 */
export interface FieldSpec {
  label: string
  operators: readonly CollectionRuleOperator[]
  /** Operators that take a list on this field. Every other operator here takes exactly one value. */
  multiValueOperators: readonly CollectionRuleOperator[]
  value: 'text' | 'integer'
  /** A closed set of values, when the field has one. Free text otherwise. */
  choices?: readonly string[]
  hint?: string
}

export const RULE_FIELDS: Readonly<Record<CollectionRuleField, FieldSpec>> = {
  Kind: {
    label: 'Kind',
    operators: ['Is', 'IsNot'],
    multiValueOperators: ['Is', 'IsNot'],
    value: 'text',
    choices: ['Movie', 'Series'],
  },
  Genre: {
    label: 'Genre',
    operators: ['Is', 'IsNot'],
    multiValueOperators: ['Is', 'IsNot'],
    value: 'text',
    hint: 'Matches when any of the title’s genres is in this list.',
  },
  ContentRating: {
    label: 'Content rating',
    operators: ['Is', 'IsNot'],
    multiValueOperators: ['Is', 'IsNot'],
    value: 'text',
    hint: 'As the metadata provider spells it — R, TV-MA, 16.',
  },
  OriginalLanguage: {
    label: 'Original language',
    operators: ['Is', 'IsNot'],
    multiValueOperators: ['Is', 'IsNot'],
    value: 'text',
    hint: 'Two-letter code, such as en or ja.',
  },
  Year: {
    label: 'Year',
    operators: ['Is', 'IsNot', 'AtLeast', 'AtMost'],
    multiValueOperators: ['Is', 'IsNot'],
    value: 'integer',
  },
  RuntimeMinutes: {
    label: 'Runtime (minutes)',
    operators: ['AtLeast', 'AtMost'],
    multiValueOperators: [],
    value: 'integer',
  },
  Title: {
    label: 'Title',
    operators: ['Contains', 'StartsWith'],
    multiValueOperators: [],
    value: 'text',
    hint: 'Plain text, not case sensitive. Not a regular expression.',
  },
}

export const RULE_FIELD_ORDER: readonly CollectionRuleField[] = [
  'Kind',
  'Genre',
  'ContentRating',
  'OriginalLanguage',
  'Year',
  'RuntimeMinutes',
  'Title',
]

export const OPERATOR_LABEL: Readonly<Record<CollectionRuleOperator, string>> = {
  Is: 'is any of',
  IsNot: 'is none of',
  AtLeast: 'is at least',
  AtMost: 'is at most',
  Contains: 'contains',
  StartsWith: 'starts with',
}

export function acceptsMultipleValues(field: CollectionRuleField, operator: CollectionRuleOperator): boolean {
  return RULE_FIELDS[field].multiValueOperators.includes(operator)
}

/** The operator to fall back to when a field changes and the chosen one is not in its vocabulary. */
export function defaultOperatorFor(field: CollectionRuleField): CollectionRuleOperator {
  return RULE_FIELDS[field].operators[0]
}

/**
 * Why this condition would be refused, or null. Deliberately the same checks the API documents, so
 * the operator is told before a round trip rather than after one — and phrased as what to do, since
 * "invalid value" is not something anyone can act on.
 */
export function conditionProblem(condition: CollectionRuleCondition): string | null {
  const spec = RULE_FIELDS[condition.field]
  if (!spec.operators.includes(condition.operator)) {
    return `${spec.label} does not support “${OPERATOR_LABEL[condition.operator]}”.`
  }
  if (condition.values.length === 0) {
    return `${spec.label} needs a value.`
  }
  if (condition.values.length > 1 && !acceptsMultipleValues(condition.field, condition.operator)) {
    return `“${OPERATOR_LABEL[condition.operator]}” takes a single value.`
  }
  if (spec.value === 'integer' && condition.values.some((value) => !/^\d+$/.test(value.trim()))) {
    return `${spec.label} takes whole numbers only.`
  }
  if (spec.choices && condition.values.some((value) => !spec.choices?.includes(value))) {
    return `${spec.label} must be one of: ${spec.choices.join(', ')}.`
  }
  return null
}

/**
 * The first problem across a whole rule, or null. A rule with no conditions is called out by name
 * because it is the dangerous one: it matches every title in the library, and the shelf it moves
 * them to decides who can see them.
 */
export function ruleProblem(conditions: readonly CollectionRuleCondition[]): string | null {
  if (conditions.length === 0) {
    return 'A rule with no conditions would match every title in the library. Add a condition.'
  }
  for (const condition of conditions) {
    const problem = conditionProblem(condition)
    if (problem) return problem
  }
  return null
}
