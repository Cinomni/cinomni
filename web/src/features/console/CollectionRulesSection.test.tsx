import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi } from '@/api/endpoints'
import type { Collection, RulePreview } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { CollectionRulesSection } from './CollectionRulesSection'

vi.mock('@/api/endpoints', () => ({
  catalogApi: {
    collectionRules: vi.fn(),
    setCollectionRules: vi.fn(),
    previewCollectionRules: vi.fn(),
  },
}))

function aCollection(overrides: Partial<Collection> = {}): Collection {
  return {
    id: 'open-1',
    name: 'Family',
    kind: 'Mixed',
    accessMode: 'Open',
    isDefault: false,
    workCount: 12,
    rulePriority: 1,
    ...overrides,
  }
}

const RESTRICTED = aCollection({ id: 'restricted-1', name: 'Adults', accessMode: 'Restricted' })

function aPreview(overrides: Partial<RulePreview> = {}): RulePreview {
  return {
    matched: 4,
    wouldMove: 3,
    pinnedSkipped: 1,
    works: [
      {
        id: 'work-1',
        title: 'A Film',
        year: 2011,
        kind: 'Movie',
        currentCollectionId: 'open-1',
        currentCollectionName: 'Family',
        targetCollectionId: 'open-1',
        targetCollectionName: 'Family',
        pinned: false,
      },
    ],
    ...overrides,
  }
}

/** Adds one valid condition so the rule stops being the match-everything case. */
async function addGenreCondition(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Add condition' }))
  await user.selectOptions(screen.getByLabelText('Field'), 'Genre')
  await user.type(screen.getByLabelText('Values, one per line'), 'Horror')
}

beforeEach(() => {
  vi.mocked(catalogApi.collectionRules).mockResolvedValue([])
  vi.mocked(catalogApi.previewCollectionRules).mockResolvedValue(aPreview())
  vi.mocked(catalogApi.setCollectionRules).mockResolvedValue({ moved: 3 })
})

describe('CollectionRulesSection', () => {
  it('CollectionRulesSection_refuses_to_save_before_the_rules_have_been_previewed', async () => {
    // Saving can move hundreds of titles between shelves, and a shelf decides who may see what is on
    // it. The preview is the safeguard, not a convenience offered next to the save.
    const user = userEvent.setup()
    const collection = aCollection()
    renderWithProviders(<CollectionRulesSection collection={collection} collections={[collection]} />)

    await addGenreCondition(user)

    expect(screen.getByRole('button', { name: 'Save rules' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Preview' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save rules' })).toBeEnabled())
  })

  it('CollectionRulesSection_previews_exactly_the_rule_set_it_would_save', async () => {
    // The preview has to answer for what Save sends, or it previews one thing and saves another.
    const user = userEvent.setup()
    const collection = aCollection()
    renderWithProviders(<CollectionRulesSection collection={collection} collections={[collection]} />)

    await addGenreCondition(user)
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    await waitFor(() =>
      expect(catalogApi.previewCollectionRules).toHaveBeenCalledWith(collection.id, [
        expect.objectContaining({
          collectionId: collection.id,
          conditions: [{ field: 'Genre', operator: 'Is', values: ['Horror'] }],
        }),
      ]),
    )
  })

  it('CollectionRulesSection_separates_what_matches_from_what_would_actually_move', async () => {
    // A pinned title matches and stays; one already on this shelf matches and has nowhere to go.
    // Reporting only "4 match" would misstate the consequence.
    const user = userEvent.setup()
    const collection = aCollection()
    renderWithProviders(<CollectionRulesSection collection={collection} collections={[collection]} />)

    await addGenreCondition(user)
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    expect(await screen.findByText('Would move')).toBeInTheDocument()
    expect(screen.getByText('Pinned, left alone')).toBeInTheDocument()
  })

  it('CollectionRulesSection_warns_when_the_rules_would_expose_restricted_titles', async () => {
    // The dangerous direction: moving OUT of a restricted shelf INTO an open one shows titles to the
    // whole household. Nothing else in the flow says so.
    const user = userEvent.setup()
    const target = aCollection()
    vi.mocked(catalogApi.previewCollectionRules).mockResolvedValue(
      aPreview({
        works: [
          {
            id: 'work-2',
            title: 'Something Restricted',
            year: 2019,
            kind: 'Movie',
            currentCollectionId: RESTRICTED.id,
            currentCollectionName: RESTRICTED.name,
            targetCollectionId: 'open-1',
            targetCollectionName: 'Family',
            pinned: false,
          },
        ],
      }),
    )
    renderWithProviders(<CollectionRulesSection collection={target} collections={[target, RESTRICTED]} />)

    await addGenreCondition(user)
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    expect(await screen.findByText('This widens who can see these titles')).toBeInTheDocument()
  })

  it('CollectionRulesSection_does_not_warn_when_nothing_leaves_a_restricted_shelf', async () => {
    // The contrast case: titles already on an open shelf are not newly exposed by moving.
    const user = userEvent.setup()
    const target = aCollection()
    renderWithProviders(<CollectionRulesSection collection={target} collections={[target, RESTRICTED]} />)

    await addGenreCondition(user)
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    await screen.findByText('Would move')
    expect(screen.queryByText('This widens who can see these titles')).not.toBeInTheDocument()
  })

  it('CollectionRulesSection_states_the_consequence_before_applying', async () => {
    // The confirmation names the shelf and who will see the titles on it, rather than asking "are
    // you sure" about an operation whose effect is invisible from the form.
    const user = userEvent.setup()
    const collection = aCollection()
    renderWithProviders(<CollectionRulesSection collection={collection} collections={[collection]} />)

    await addGenreCondition(user)
    await user.click(screen.getByRole('button', { name: 'Preview' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save rules' })).toBeEnabled())
    await user.click(screen.getByRole('button', { name: 'Save rules' }))

    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/every account in the household will see them/)).toBeInTheDocument()
    expect(catalogApi.setCollectionRules).not.toHaveBeenCalled()

    await user.click(dialog.getByRole('button', { name: 'Apply rules' }))
    await waitFor(() => expect(catalogApi.setCollectionRules).toHaveBeenCalled())
  })

  it('CollectionRulesSection_will_not_preview_a_rule_that_matches_everything', async () => {
    // A rule with no conditions claims the whole library. The API refuses it; this refuses it first,
    // and says what it would have done rather than reporting a form error.
    const user = userEvent.setup()
    const collection = aCollection()
    renderWithProviders(<CollectionRulesSection collection={collection} collections={[collection]} />)

    await user.click(await screen.findByRole('button', { name: 'Add condition' }))
    await user.click(screen.getByRole('button', { name: /^Remove/ }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/every title in the library/)
    expect(screen.getByRole('button', { name: 'Preview' })).toBeDisabled()
  })

  it('CollectionRulesSection_drops_values_that_a_narrowed_operator_could_not_carry', async () => {
    // Year takes a list for "is any of" and one value for "at least". Switching must not leave a
    // list behind for the API to reject.
    const user = userEvent.setup()
    const collection = aCollection()
    renderWithProviders(<CollectionRulesSection collection={collection} collections={[collection]} />)

    await user.click(await screen.findByRole('button', { name: 'Add condition' }))
    await user.selectOptions(screen.getByLabelText('Field'), 'Year')
    await user.type(screen.getByLabelText('Values, one per line'), '1990\n1991')
    await user.selectOptions(screen.getByLabelText('Operator'), 'AtLeast')

    // Assert — a single field now, carrying only the first value.
    expect(screen.getByLabelText('Value')).toHaveValue('1990')
    expect(screen.queryByLabelText('Values, one per line')).not.toBeInTheDocument()
  })
})
