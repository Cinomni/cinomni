// These tests assert what the CLIENT offers: whether the console sub-navigation renders for the
// signed-in user's role. `RequireAdmin` is an experience guard only — the actual authorization
// boundary is the administrator policy enforced by the API behind each console endpoint. A green
// test here proves the SPA does not dangle the console UI in front of a member; it proves nothing
// about server-side enforcement.
import { describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import { useAuth } from '@/auth/useAuth'
import { aCurrentUser, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { CONSOLE_SECTIONS, ConsoleLayout } from './ConsoleLayout'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))

function signedInAs(isAdministrator: boolean) {
  const value = anAuthContext(aCurrentUser({
      id: 'u-1',
      username: 'someone',
      isAdministrator,
      role: isAdministrator ? 'Administrator' : 'Member',
      permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
}

describe('ConsoleLayout', () => {
  it('ConsoleLayout_shows_the_RequireAdmin_explanation_and_withholds_the_sub_navigation_from_a_member', () => {
    // Arrange
    signedInAs(false)

    // Act
    renderWithProviders(<ConsoleLayout />)

    // Assert — the member meets one explanation, not a sub-navigation for a console they cannot use.
    expect(screen.getByText('Administrators only')).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Console sections' })).not.toBeInTheDocument()
    for (const section of CONSOLE_SECTIONS) {
      expect(screen.queryByRole('link', { name: section.label })).not.toBeInTheDocument()
    }
  })

  it('ConsoleLayout_lists_every_CONSOLE_SECTIONS_entry_for_an_administrator', () => {
    // Arrange
    signedInAs(true)

    // Act
    renderWithProviders(<ConsoleLayout />)

    // Assert — derived from the exported constant, so a new section cannot silently escape this test.
    const nav = screen.getByRole('navigation', { name: 'Console sections' })
    for (const section of CONSOLE_SECTIONS) {
      expect(within(nav).getByRole('link', { name: section.label })).toBeInTheDocument()
    }
    expect(within(nav).getAllByRole('link')).toHaveLength(CONSOLE_SECTIONS.length)
  })
})
