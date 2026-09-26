import { describe, expect, it } from 'vitest'
import type { CollectionRuleCondition } from '@/api/types'
import { RULE_FIELDS, acceptsMultipleValues, conditionProblem, ruleProblem } from './collectionRules'

function aCondition(overrides: Partial<CollectionRuleCondition> = {}): CollectionRuleCondition {
  return { field: 'Genre', operator: 'Is', values: ['Horror'], ...overrides }
}

describe('collection rule vocabulary', () => {
  it('collectionRules_rejects_an_operator_the_field_does_not_accept', () => {
    // A runtime cannot be "contains": the API answers operator_not_allowed, and this is what stops
    // the combination being assembled in the first place.
    expect(conditionProblem(aCondition({ field: 'RuntimeMinutes', operator: 'Contains', values: ['90'] })))
      .toMatch(/does not support/)
  })

  it('collectionRules_rejects_a_list_where_the_operator_takes_one_value', () => {
    expect(conditionProblem(aCondition({ field: 'Year', operator: 'AtLeast', values: ['1990', '1991'] })))
      .toMatch(/takes a single value/)
  })

  it('collectionRules_accepts_a_list_where_the_operator_does_take_one', () => {
    expect(conditionProblem(aCondition({ field: 'Year', operator: 'Is', values: ['1990', '1991'] }))).toBeNull()
  })

  it('collectionRules_rejects_a_year_that_is_not_a_whole_number', () => {
    // Every value travels as a string, so nothing else would catch this before the server did.
    expect(conditionProblem(aCondition({ field: 'Year', operator: 'Is', values: ['nineteen ninety'] })))
      .toMatch(/whole numbers/)
  })

  it('collectionRules_rejects_a_value_outside_a_closed_choice_set', () => {
    expect(conditionProblem(aCondition({ field: 'Kind', operator: 'Is', values: ['Album'] })))
      .toMatch(/must be one of/)
  })

  it('collectionRules_rejects_a_condition_with_no_value', () => {
    expect(conditionProblem(aCondition({ values: [] }))).toMatch(/needs a value/)
  })

  it('collectionRules_names_the_empty_rule_as_the_dangerous_one', () => {
    // A rule with no conditions matches the whole library, and the shelf it moves titles to decides
    // who may see them. "no_conditions" deserves more than a generic invalid-form message.
    expect(ruleProblem([])).toMatch(/every title in the library/)
  })

  it('collectionRules_reports_the_first_problem_in_a_rule', () => {
    expect(ruleProblem([aCondition(), aCondition({ field: 'Year', values: ['soon'] })]))
      .toMatch(/whole numbers/)
  })

  it('collectionRules_mirrors_which_operators_take_lists', () => {
    // The multi/single split is per field AND operator, not per field: Year takes a list for "is any
    // of" and a single value for "at least".
    expect(acceptsMultipleValues('Year', 'Is')).toBe(true)
    expect(acceptsMultipleValues('Year', 'AtLeast')).toBe(false)
    expect(acceptsMultipleValues('Title', 'Contains')).toBe(false)
  })

  it('collectionRules_offers_no_field_for_availability_or_status', () => {
    // Absent on purpose: a rule on availability would move a title between shelves — and change who
    // can see it — as a side effect of a download finishing.
    const fields = Object.keys(RULE_FIELDS)
    expect(fields).not.toContain('HasAsset')
    expect(fields).not.toContain('Status')
    expect(fields).not.toContain('AvailableEpisodeCount')
  })
})
