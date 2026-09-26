// These tests assert what the CLIENT offers: which nav destinations a signed-in user sees and
// whether the pending-requests binding is called at all. They are not proof the API is protected —
// the actual authorization boundary is the administrator policy on each endpoint (downloads,
// activity, console, requests/pending-count). A green test here proves the SPA does not dangle an
// operator-only control, or its supporting request, in front of a member; nothing more.
import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { requestsApi, notificationsApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import type { AuthContextValue } from '@/auth/context'
import { aCurrentUser, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { AppLayout } from './AppLayout'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))
vi.mock('@/api/endpoints', () => ({
  requestsApi: { pendingCount: vi.fn() },
  notificationsApi: { unreadCount: vi.fn() },
  // The layout renders the build footer. Left unmocked it would still pass — the footer swallows a
  // failed read by design — but the test would be passing for the wrong reason.
  systemApi: { info: vi.fn() },
}))

function signedInAs(isAdministrator: boolean): AuthContextValue {
  const value = anAuthContext(aCurrentUser({
      id: 'u-1',
      username: 'someone',
      isAdministrator,
      role: isAdministrator ? 'Administrator' : 'Member',
      permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
  return value
}

describe('AppLayout landmarks', () => {
  it('AppLayout_lets_a_keyboard_user_skip_the_navigation_to_reach_the_page', async () => {
    // Arrange — seven nav links stand between Tab and the content on every navigation.
    signedInAs(false)
    vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })

    // Act
    renderWithProviders(<AppLayout />)

    // Assert — the link exists and actually points at the main landmark, not at nothing.
    const skip = await screen.findByRole('link', { name: 'Skip to main content' })
    expect(skip).toHaveAttribute('href', '#main-content')
    expect(document.getElementById('main-content')?.tagName).toBe('MAIN')
  })

  it('AppLayout_names_its_navigation_landmarks', async () => {
    // Arrange — the console renders a second nav ("Console sections"); an unnamed main nav leaves a
    // screen-reader user choosing between two identical "navigation" landmarks.
    signedInAs(false)
    vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })

    // Act
    renderWithProviders(<AppLayout />)

    // Assert — both responsive renderings are landmarks and both carry the same name; only one of
    // them is ever displayed, so only one ever reaches the accessibility tree.
    expect(await screen.findAllByRole('navigation', { name: 'Main' })).toHaveLength(2)
  })
})

describe('AppLayout', () => {
  it('AppLayout_offers_the_operator_destinations_to_an_administrator', async () => {
    // Arrange
    signedInAs(true)
    vi.mocked(requestsApi.pendingCount).mockResolvedValue({ count: 0 })
    vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })

    // Act
    renderWithProviders(<AppLayout />)

    // Assert — present for an administrator, once per responsive nav rendering.
    expect(await screen.findAllByRole('link', { name: 'Activity' })).not.toHaveLength(0)
    // Transfers are shown under their titles in Activity; there is no separate destination for them.
    expect(screen.queryByRole('link', { name: 'Downloads' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Administration' })).not.toHaveLength(0)
  })

  it('AppLayout_withholds_the_operator_destinations_from_a_member', async () => {
    // Arrange
    signedInAs(false)
    vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })

    // Act
    renderWithProviders(<AppLayout />)

    // Assert — a member's client does not offer these destinations at all.
    await screen.findAllByRole('link', { name: 'Home' })
    expect(screen.queryByRole('link', { name: 'Downloads' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Activity' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Administration' })).not.toBeInTheDocument()
  })

  it('AppLayout_fetches_and_shows_the_pending_requests_badge_only_for_an_administrator', async () => {
    // Arrange
    signedInAs(true)
    vi.mocked(requestsApi.pendingCount).mockResolvedValue({ count: 5 })
    vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })

    // Act
    renderWithProviders(<AppLayout />)

    // Assert — the mobile nav link's accessible name carries the pending count as prose.
    expect(await screen.findByRole('link', { name: 'Requests, 5 pending' })).toBeInTheDocument()
  })

  it('AppLayout_never_calls_the_operator_only_pending_requests_endpoint_for_a_member', async () => {
    // Arrange
    signedInAs(false)
    vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })

    // Act
    renderWithProviders(<AppLayout />)

    // Assert — a member's client never dispatches the operator-only request in the first place.
    await screen.findAllByRole('link', { name: 'Requests' })
    expect(requestsApi.pendingCount).not.toHaveBeenCalled()
  })
})
