import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi } from '@/api/endpoints'
import type { Collection, Work, WorkPage } from '@/api/types'
import { aWork } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { CollectionPlacementSection } from './CollectionPlacementSection'

vi.mock('@/api/endpoints', () => ({
  catalogApi: {
    page: vi.fn(),
    moveWork: vi.fn(),
    unpinWork: vi.fn(),
  },
}))

const library: Collection = {
  id: 'library',
  name: 'Library',
  kind: 'Mixed',
  accessMode: 'Open',
  isDefault: true,
  workCount: 2,
  rulePriority: 0,
}

const grownUps: Collection = {
  id: 'grown-ups',
  name: 'Grown-ups',
  kind: 'Movies',
  accessMode: 'Restricted',
  isDefault: false,
  workCount: 1,
  rulePriority: 1,
}

const alien = aWork({
  id: 'alien',
  title: 'Alien',
  year: 1979,
  kind: 'Movie',
  collectionId: grownUps.id,
  collectionPinned: true,
})

function aPage(items: Work[], total = items.length): WorkPage {
  return { items, total, offset: 0, limit: 20 }
}

beforeEach(() => {
  vi.mocked(catalogApi.page).mockReset()
  vi.mocked(catalogApi.moveWork).mockReset()
  vi.mocked(catalogApi.unpinWork).mockReset()
  vi.mocked(catalogApi.page).mockResolvedValue(aPage([alien]))
  vi.mocked(catalogApi.moveWork).mockResolvedValue(undefined)
  vi.mocked(catalogApi.unpinWork).mockResolvedValue(undefined)
})

describe('CollectionPlacementSection', () => {
  it('CollectionPlacementSection_does_not_search_until_asked_and_then_shows_the_shelf_and_the_pin', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CollectionPlacementSection collections={[library, grownUps]} />)

    expect(catalogApi.page).not.toHaveBeenCalled()
    expect(screen.getByText('Search for a title, or show the ones already pinned.')).toBeInTheDocument()

    await user.type(screen.getByLabelText('Find a title'), 'Alien')

    expect(await screen.findByText('On Grown-ups')).toBeInTheDocument()
    expect(screen.getByText('Pinned')).toBeInTheDocument()
    expect(screen.getByText('Restricted')).toBeInTheDocument()
    expect(catalogApi.page).toHaveBeenCalledWith({ q: 'Alien' }, 0, 20, expect.any(AbortSignal))
  })

  it('CollectionPlacementSection_warns_before_a_move_that_would_show_the_title_to_more_people', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CollectionPlacementSection collections={[library, grownUps]} />)
    await user.type(screen.getByLabelText('Find a title'), 'Alien')
    await user.click(await screen.findByRole('button', { name: 'Move Alien (1979)' }))

    const dialog = await screen.findByRole('dialog', { name: 'Move Alien (1979)?' })
    expect(catalogApi.moveWork).not.toHaveBeenCalled()

    await user.selectOptions(within(dialog).getByLabelText('Collection'), library.id)

    expect(within(dialog).getByText(/More people would be able to see it/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Move and pin' }))

    expect(catalogApi.moveWork).toHaveBeenCalledWith('alien', 'library')
  })

  it('CollectionPlacementSection_releases_a_pin_only_after_it_says_the_rules_will_place_the_title', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CollectionPlacementSection collections={[library, grownUps]} />)
    await user.type(screen.getByLabelText('Find a title'), 'Alien')
    await user.click(await screen.findByRole('button', { name: 'Release pin on Alien (1979)' }))

    const dialog = await screen.findByRole('dialog', { name: 'Release the pin on Alien (1979)?' })
    expect(within(dialog).getByText(/The rules place it wherever they say now/)).toBeInTheDocument()
    expect(catalogApi.unpinWork).not.toHaveBeenCalled()

    await user.click(within(dialog).getByRole('button', { name: 'Release pin' }))

    expect(catalogApi.unpinWork).toHaveBeenCalledWith('alien')
  })

  it('CollectionPlacementSection_says_so_when_the_search_fails_rather_than_showing_no_titles', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.page).mockRejectedValue(new Error('network down'))
    renderWithProviders(<CollectionPlacementSection collections={[library, grownUps]} />)

    await user.type(screen.getByLabelText('Find a title'), 'Alien')

    expect(await screen.findByText('network down')).toBeInTheDocument()
    expect(screen.queryByText('No titles match.')).not.toBeInTheDocument()
  })

  it('CollectionPlacementSection_stays_open_and_says_why_when_the_server_refuses_the_move', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.moveWork).mockRejectedValue(new Error('That shelf is gone.'))
    renderWithProviders(<CollectionPlacementSection collections={[library, grownUps]} />)
    await user.type(screen.getByLabelText('Find a title'), 'Alien')
    await user.click(await screen.findByRole('button', { name: 'Move Alien (1979)' }))

    const dialog = await screen.findByRole('dialog', { name: 'Move Alien (1979)?' })
    await user.click(within(dialog).getByRole('button', { name: 'Move and pin' }))

    expect(await within(dialog).findByText('That shelf is gone.')).toBeInTheDocument()
    expect(dialog).toBeInTheDocument()
  })
})
