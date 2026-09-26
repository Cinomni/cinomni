import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { useAuth } from '@/auth/useAuth'
import { aCurrentUser, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { RequireAdmin } from './RequireAdmin'

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

describe('RequireAdmin', () => {
  it('RequireAdmin_hides_the_operator_surface_from_a_member', () => {
    // Arrange — a member who followed a shared link to an operator page.
    signedInAs(false)

    // Act
    renderWithProviders(
      <RequireAdmin>
        <p>Outbox backlog</p>
      </RequireAdmin>,
    )

    // Assert — one explanation, not a page whose every panel answers 403.
    expect(screen.queryByText('Outbox backlog')).not.toBeInTheDocument()
    expect(screen.getByText('Administrators only')).toBeInTheDocument()
  })

  it('RequireAdmin_renders_the_surface_for_an_administrator', () => {
    // Arrange
    signedInAs(true)

    // Act
    renderWithProviders(
      <RequireAdmin>
        <p>Outbox backlog</p>
      </RequireAdmin>,
    )

    // Assert
    expect(screen.getByText('Outbox backlog')).toBeInTheDocument()
  })
})
