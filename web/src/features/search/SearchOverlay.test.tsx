import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useLocation } from 'react-router'
import { catalogApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import { aCurrentUser, aWork, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { SearchOverlay } from './SearchOverlay'

vi.mock('@/api/endpoints', () => ({ catalogApi: { list: vi.fn() } }))
vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))

function signedIn({ isAdministrator = false, canRequest = true } = {}) {
  vi.mocked(useAuth).mockReturnValue(
    anAuthContext(
      aCurrentUser({
        isAdministrator,
        role: isAdministrator ? 'Administrator' : 'Member',
        permissions: { canRequest, requestsAutoApproved: false, openRequestLimit: null },
      }),
    ),
  )
}

function Location() {
  return <p data-testid="location">{useLocation().pathname + useLocation().search}</p>
}

function renderOverlay(onClose = vi.fn()) {
  renderWithProviders(
    <>
      <SearchOverlay open onClose={onClose} />
      <Location />
    </>,
  )
  return { onClose }
}

describe('SearchOverlay', () => {
  it('SearchOverlay_finds_library_titles_first_and_opens_one_with_Enter', async () => {
    // Arrange
    signedIn()
    vi.mocked(catalogApi.list).mockResolvedValue([
      aWork({ id: 'w1', title: 'Arrival' }),
      aWork({ id: 'w2', kind: 'Series', title: 'Dark' }),
      aWork({ id: 'w3', title: 'The Dark Knight' }),
    ])
    const user = userEvent.setup()
    const { onClose } = renderOverlay()

    // Act — "dark" starts one title and appears inside another; the one it starts wins.
    await user.type(await screen.findByRole('combobox', { name: 'Search titles and pages' }), 'dark')
    const options = await screen.findAllByRole('option')
    await user.keyboard('{Enter}')

    // Assert
    expect(options[0]).toHaveTextContent('Dark')
    expect(options[1]).toHaveTextContent('The Dark Knight')
    expect(screen.queryByRole('option', { name: /Arrival/ })).not.toBeInTheDocument()
    expect(onClose).toHaveBeenCalled()
    expect(screen.getByTestId('location')).toHaveTextContent('/series/w2')
  })

  it('SearchOverlay_hands_the_term_to_the_add_page_instead_of_calling_providers_per_keystroke', async () => {
    // Arrange
    signedIn()
    vi.mocked(catalogApi.list).mockResolvedValue([])
    const user = userEvent.setup()
    renderOverlay()

    // Act
    await user.type(await screen.findByRole('combobox'), 'Past Lives')
    await user.click(screen.getByRole('option', { name: /Request a movie called “Past Lives”/ }))

    // Assert — a member requests; the term travels in the URL, encoded.
    expect(screen.getByTestId('location')).toHaveTextContent('/add?q=Past%20Lives')
  })

  it('SearchOverlay_offers_no_provider_action_to_an_account_that_cannot_request', async () => {
    signedIn({ canRequest: false })
    vi.mocked(catalogApi.list).mockResolvedValue([])
    const user = userEvent.setup()
    renderOverlay()

    await user.type(await screen.findByRole('combobox'), 'Past Lives')

    expect(screen.queryByRole('option', { name: /movie called/ })).not.toBeInTheDocument()
  })

  it('SearchOverlay_lists_administration_pages_only_for_an_administrator', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.list).mockResolvedValue([])

    signedIn()
    const { unmount } = renderWithProviders(<SearchOverlay open onClose={vi.fn()} />)
    await user.type(await screen.findByRole('combobox'), 'index')
    expect(screen.queryByRole('option', { name: /Indexers/ })).not.toBeInTheDocument()
    unmount()

    signedIn({ isAdministrator: true })
    renderWithProviders(<SearchOverlay open onClose={vi.fn()} />)
    await user.type(await screen.findByRole('combobox'), 'index')
    expect(await screen.findByRole('option', { name: /Administration › Indexers/ })).toBeInTheDocument()
  })
})
