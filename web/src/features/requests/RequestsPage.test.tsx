import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { requestsApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import type { MediaRequest } from '@/api/types'
import { aCurrentUser, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { RequestsPage } from './RequestsPage'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))
vi.mock('@/api/endpoints', () => ({
  requestsApi: {
    list: vi.fn(),
    approve: vi.fn(),
    reject: vi.fn(),
  },
}))

function signedInAsAdmin() {
  const value = anAuthContext(aCurrentUser({
      id: 'admin-1',
      username: 'root',
      isAdministrator: true,
      role: 'Administrator',
      permissions: { canRequest: true, requestsAutoApproved: true, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
}

function signedInAsMember() {
  const value = anAuthContext(aCurrentUser({
      id: 'user-1',
      username: 'nadia',
      isAdministrator: false,
      role: 'Member',
      permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
}

function aRequest(overrides: Partial<MediaRequest> = {}): MediaRequest {
  return {
    id: 'req-1',
    title: 'Arrival',
    year: 2016,
    provider: 'Tmdb',
    externalId: '329865',
    kind: 'Movie',
    status: 'Pending',
    requestedByUserId: 'user-1',
    requestedByUsername: 'nadia',
    workId: null,
    decisionNote: null,
    requestedAt: '2026-07-29T10:00:00Z',
    decidedAt: null,
    ...overrides,
  }
}

describe('RequestsPage cap notice', () => {
  it('RequestsPage_shows_the_cap_notice_when_the_returned_page_is_exactly_the_limit', async () => {
    // Arrange — requestsApi.list has no offset, so a page that comes back exactly at the default
    // limit (100) is the one case the endpoint cannot tell apart from "there is more".
    signedInAsAdmin()
    const fullPage = Array.from({ length: 100 }, (_, index) =>
      aRequest({ id: `req-${index}`, requestedByUserId: 'user-1', requestedByUsername: 'nadia' }),
    )
    vi.mocked(requestsApi.list).mockResolvedValue(fullPage)

    // Act
    renderWithProviders(<RequestsPage />)

    // Assert
    expect(await screen.findByText(/showing the most recent 100 requests/i)).toBeInTheDocument()
    expect(screen.getByText(/there may be more/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /show more/i })).toBeInTheDocument()
  })

  it('RequestsPage_hides_the_cap_notice_when_the_returned_page_is_below_the_limit', async () => {
    // Arrange — a short page proves the fetch already saw everything there is.
    signedInAsAdmin()
    vi.mocked(requestsApi.list).mockResolvedValue([
      aRequest({ id: 'req-1', title: 'Arrival' }),
      aRequest({ id: 'req-2', title: 'Dune' }),
    ])

    // Act
    renderWithProviders(<RequestsPage />)
    await screen.findByText('Arrival')

    // Assert
    expect(screen.queryByText(/there may be more/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /show more/i })).not.toBeInTheDocument()
  })
})

describe('RequestsPage requester filter', () => {
  it('RequestsPage_hides_the_requester_filter_for_a_member', async () => {
    // Arrange — a member's own requests are already scoped by the API to just themselves, so a
    // filter here would be a control with exactly one possible value.
    signedInAsMember()
    vi.mocked(requestsApi.list).mockResolvedValue([aRequest({ requestedByUserId: 'user-1', requestedByUsername: 'nadia' })])

    // Act
    renderWithProviders(<RequestsPage />)
    await screen.findByText('Arrival')

    // Assert
    expect(screen.queryByLabelText('Filter by requester')).not.toBeInTheDocument()
  })

  it('RequestsPage_hides_the_requester_filter_for_an_administrator_when_only_one_requester_is_present', async () => {
    // Arrange — even for an administrator, a household of one visible requester is not a real filter.
    signedInAsAdmin()
    vi.mocked(requestsApi.list).mockResolvedValue([
      aRequest({ id: 'req-1', title: 'Arrival', requestedByUserId: 'user-1', requestedByUsername: 'nadia' }),
      aRequest({ id: 'req-2', title: 'Dune', requestedByUserId: 'user-1', requestedByUsername: 'nadia' }),
    ])

    // Act
    renderWithProviders(<RequestsPage />)
    await screen.findByText('Arrival')

    // Assert
    expect(screen.queryByLabelText('Filter by requester')).not.toBeInTheDocument()
  })

  it('RequestsPage_shows_the_requester_filter_for_an_administrator_and_narrows_the_list_by_it', async () => {
    // Arrange — this page is shared with members, so the roster shown must come only from the
    // requests already fetched, never from a separate accounts call.
    signedInAsAdmin()
    vi.mocked(requestsApi.list).mockResolvedValue([
      aRequest({ id: 'req-1', title: 'Arrival', requestedByUserId: 'user-1', requestedByUsername: 'nadia' }),
      aRequest({ id: 'req-2', title: 'Dune', requestedByUserId: 'user-2', requestedByUsername: 'omar' }),
    ])
    const user = userEvent.setup()

    // Act
    renderWithProviders(<RequestsPage />)
    await screen.findByText('Arrival')
    const select = screen.getByLabelText('Filter by requester')
    await user.selectOptions(select, 'omar')

    // Assert
    expect(screen.getByText('Dune')).toBeInTheDocument()
    expect(screen.queryByText('Arrival')).not.toBeInTheDocument()
  })
})
