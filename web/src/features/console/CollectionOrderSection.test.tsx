import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi } from '@/api/endpoints'
import type { Collection, RulePreview } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { CollectionOrderSection } from './CollectionOrderSection'

vi.mock('@/api/endpoints', () => ({
  catalogApi: {
    setCollectionRulePriority: vi.fn(),
    previewCollectionRulePriority: vi.fn(),
  },
}))

function aCollection(overrides: Partial<Collection>): Collection {
  return {
    id: 'c-1',
    name: 'One',
    kind: 'Mixed',
    accessMode: 'Open',
    isDefault: false,
    workCount: 3,
    rulePriority: 1,
    ...overrides,
  }
}

const FAMILY = aCollection({ id: 'family', name: 'Family', rulePriority: 1 })
const ADULTS = aCollection({ id: 'adults', name: 'Adults', accessMode: 'Restricted', rulePriority: 2 })
const ARCHIVE = aCollection({ id: 'archive', name: 'Archive', rulePriority: 3 })
const ALL = [FAMILY, ADULTS, ARCHIVE]

function aPreview(overrides: Partial<RulePreview> = {}): RulePreview {
  return {
    matched: 2,
    wouldMove: 2,
    pinnedSkipped: 0,
    works: [
      {
        id: 'work-1',
        title: 'A Film',
        year: 2019,
        kind: 'Movie',
        currentCollectionId: FAMILY.id,
        currentCollectionName: FAMILY.name,
        targetCollectionId: ARCHIVE.id,
        targetCollectionName: ARCHIVE.name,
        pinned: false,
      },
    ],
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(catalogApi.previewCollectionRulePriority).mockResolvedValue(aPreview())
  vi.mocked(catalogApi.setCollectionRulePriority).mockResolvedValue({ moved: 2 })
})

describe('CollectionOrderSection', () => {
  it('CollectionOrderSection_sends_the_whole_order_not_the_row_that_moved', async () => {
    // The API takes the complete list because position is the priority: a per-row write would need a
    // tie-break, and a sequence of writes would leave intermediate orders nobody chose standing.
    const user = userEvent.setup()
    renderWithProviders(<CollectionOrderSection collections={ALL} />)

    await user.click(screen.getByRole('button', { name: 'Move Adults earlier' }))
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    await waitFor(() =>
      expect(catalogApi.previewCollectionRulePriority).toHaveBeenCalledWith(['adults', 'family', 'archive']),
    )
  })

  it('CollectionOrderSection_refuses_to_save_an_order_that_has_not_been_previewed', async () => {
    // Reordering acts through overlaps between collections' rules, and no screen shows those. The
    // preview is the only way to see what a moved row does before it does it.
    const user = userEvent.setup()
    renderWithProviders(<CollectionOrderSection collections={ALL} />)

    await user.click(screen.getByRole('button', { name: 'Move Archive earlier' }))

    expect(screen.getByRole('button', { name: 'Save order' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Preview' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save order' })).toBeEnabled())
  })

  it('CollectionOrderSection_invalidates_a_preview_when_the_order_changes_again', async () => {
    // A preview describes one specific order. Leaving it standing after another move would let a
    // save be authorised by a preview of something else.
    const user = userEvent.setup()
    renderWithProviders(<CollectionOrderSection collections={ALL} />)

    await user.click(screen.getByRole('button', { name: 'Move Adults earlier' }))
    await user.click(screen.getByRole('button', { name: 'Preview' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save order' })).toBeEnabled())

    await user.click(screen.getByRole('button', { name: 'Move Archive earlier' }))

    expect(screen.getByRole('button', { name: 'Save order' })).toBeDisabled()
  })

  it('CollectionOrderSection_offers_nothing_to_save_until_something_actually_moved', async () => {
    // Rendering the order is not editing it.
    renderWithProviders(<CollectionOrderSection collections={ALL} />)

    expect(screen.queryByRole('button', { name: 'Preview' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save order' })).not.toBeInTheDocument()
  })

  it('CollectionOrderSection_announces_the_move_for_anyone_not_watching_the_rows_jump', async () => {
    // The whole reason this is buttons rather than a drag: the feedback has to reach someone using a
    // keyboard and a screen reader, because this list settles a permissions question.
    const user = userEvent.setup()
    const { container } = renderWithProviders(<CollectionOrderSection collections={ALL} />)

    await user.click(screen.getByRole('button', { name: 'Move Adults earlier' }))

    const live = container.querySelector('[aria-live="polite"]')
    expect(live).toHaveTextContent('Adults moved to position 1 of 3.')
  })

  it('CollectionOrderSection_flags_titles_that_are_restricted_today', async () => {
    // Reordering hands a title to whichever collection claims it next. One that is restricted now
    // may end up somewhere more people can see.
    const user = userEvent.setup()
    vi.mocked(catalogApi.previewCollectionRulePriority).mockResolvedValue(
      aPreview({
        works: [
          {
            id: 'work-2',
            title: 'Something Restricted',
            year: 2019,
            kind: 'Movie',
            currentCollectionId: ADULTS.id,
            currentCollectionName: ADULTS.name,
            targetCollectionId: FAMILY.id,
            targetCollectionName: FAMILY.name,
            pinned: false,
          },
        ],
      }),
    )
    renderWithProviders(<CollectionOrderSection collections={ALL} />)

    await user.click(screen.getByRole('button', { name: 'Move Adults earlier' }))
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    expect(await screen.findByText('This widens who can see these titles')).toBeInTheDocument()
  })

  it('CollectionOrderSection_counts_a_title_that_falls_back_to_the_default_as_an_exposure', async () => {
    // The case nobody anticipates: a title no rule claims any more is not moved anywhere by hand —
    // it returns to the default collection, which is open. Coming off a restricted shelf, that is an
    // exposure produced by ceasing to match, and it looks like any other destination here on purpose.
    const user = userEvent.setup()
    const DEFAULT = aCollection({ id: 'default', name: 'Library', isDefault: true, rulePriority: 9 })
    vi.mocked(catalogApi.previewCollectionRulePriority).mockResolvedValue(
      aPreview({
        works: [
          {
            id: 'work-3',
            title: 'Orphaned Title',
            year: 2003,
            kind: 'Movie',
            currentCollectionId: ADULTS.id,
            currentCollectionName: ADULTS.name,
            targetCollectionId: DEFAULT.id,
            targetCollectionName: DEFAULT.name,
            pinned: false,
          },
        ],
      }),
    )
    renderWithProviders(<CollectionOrderSection collections={[...ALL, DEFAULT]} />)

    await user.click(screen.getByRole('button', { name: 'Move Adults earlier' }))
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    expect(await screen.findByText('This widens who can see these titles')).toBeInTheDocument()
    // Scoped to the affected-titles row: the collection also appears in the order list above.
    const row = screen.getByText('Orphaned Title').closest('li')
    expect(row).toHaveTextContent('Adults')
    expect(row).toHaveTextContent('Library')
  })

  it('CollectionOrderSection_says_the_consequence_before_applying', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CollectionOrderSection collections={ALL} />)

    await user.click(screen.getByRole('button', { name: 'Move Adults earlier' }))
    await user.click(screen.getByRole('button', { name: 'Preview' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save order' })).toBeEnabled())
    await user.click(screen.getByRole('button', { name: 'Save order' }))

    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/changes\s+who can see those titles/)).toBeInTheDocument()
    expect(catalogApi.setCollectionRulePriority).not.toHaveBeenCalled()

    await user.click(dialog.getByRole('button', { name: 'Apply order' }))
    await waitFor(() =>
      expect(catalogApi.setCollectionRulePriority).toHaveBeenCalledWith(['adults', 'family', 'archive']),
    )
  })
})
