import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, identityApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import type { UserAccount } from '@/api/types'
import { aCurrentUser, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { UsersPage } from './UsersPage'

vi.mock('@/api/endpoints', () => ({
  identityApi: {
    users: vi.fn(),
    createUser: vi.fn(),
    setUserRole: vi.fn(),
    setUserPermissions: vi.fn(),
    setUserDisabled: vi.fn(),
  },
  catalogApi: {
    contentRatings: vi.fn(),
  },
}))
vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))

function aMember(overrides: Partial<UserAccount> = {}): UserAccount {
  return {
    id: 'user-1',
    username: 'nadia',
    role: 'Member',
    isAdministrator: false,
    permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null },
    isDisabled: false,
    createdAt: '2026-01-01T00:00:00Z',
    lastLoginAt: null,
    ...overrides,
  }
}

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

beforeEach(() => {
  signedInAsAdmin()
  // No existing accounts, so the list renders empty and only the "Add user" flow is under test.
  vi.mocked(identityApi.users).mockResolvedValue([])
  vi.mocked(identityApi.createUser).mockResolvedValue({ userId: 'user-2' })
  vi.mocked(identityApi.setUserRole).mockResolvedValue(undefined)
  vi.mocked(catalogApi.contentRatings).mockResolvedValue({ region: null, certificates: [] })
})

describe('UsersPage open-request limit', () => {
  it('UsersPage_sets_a_content_ceiling_from_the_servers_ladder', async () => {
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    vi.mocked(identityApi.setUserPermissions).mockResolvedValue(undefined)
    vi.mocked(catalogApi.contentRatings).mockResolvedValue({ region: 'ES', certificates: ['A', '7', '12', '16', '18'] })
    signedInAsAdmin()

    renderWithProviders(<UsersPage />)
    await user.selectOptions(await screen.findByLabelText('Content ceiling'), '12')

    await waitFor(() =>
      expect(identityApi.setUserPermissions).toHaveBeenCalledWith('user-1', expect.objectContaining({ contentCeiling: '12' })),
    )
  })

  it('UsersPage_distinguishes_deferring_to_the_installation_from_having_no_limit', async () => {
    // Arrange — null and 0 are different decisions that a number-only control would flatten: defer
    // to whatever the installation says, versus override it and cap nothing.
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    signedInAsAdmin()

    // Act
    renderWithProviders(<UsersPage />)

    // Assert — a null limit reads as deferring, not as unlimited.
    expect(await screen.findByLabelText('Open requests')).toHaveValue('default')
  })

  it('UsersPage_sends_null_zero_or_the_count_for_the_three_choices', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    vi.mocked(identityApi.setUserPermissions).mockResolvedValue(undefined)
    signedInAsAdmin()
    renderWithProviders(<UsersPage />)
    const mode = await screen.findByLabelText('Open requests')

    // Act — a cap of five.
    await user.selectOptions(mode, 'capped')
    await user.type(screen.getByLabelText('Requests'), '5')
    await user.click(screen.getByRole('button', { name: 'Apply' }))

    // Assert
    await waitFor(() =>
      expect(identityApi.setUserPermissions).toHaveBeenCalledWith('user-1', {
        canRequest: true,
        requestsAutoApproved: false,
        openRequestLimit: 5,
        contentCeiling: null,
      }),
    )

    // Act — and "no limit" is zero, not an empty field.
    await user.selectOptions(mode, 'unlimited')
    await user.click(screen.getByRole('button', { name: 'Apply' }))

    // Assert
    await waitFor(() =>
      expect(identityApi.setUserPermissions).toHaveBeenLastCalledWith('user-1', {
        canRequest: true,
        requestsAutoApproved: false,
        openRequestLimit: 0,
        contentCeiling: null,
      }),
    )
  })

  it('UsersPage_refuses_a_cap_that_is_not_a_whole_number_before_calling_the_API', async () => {
    // Arrange — a cap of zero typed by hand means "no limit" on the wire, which is the opposite of
    // what someone typing 0 into a box labelled "At most" is asking for.
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    signedInAsAdmin()
    renderWithProviders(<UsersPage />)

    // Act
    await user.selectOptions(await screen.findByLabelText('Open requests'), 'capped')
    await user.type(screen.getByLabelText('Requests'), '0')
    await user.click(screen.getByRole('button', { name: 'Apply' }))

    // Assert
    expect(await screen.findByText(/whole number from 1 to 1000/)).toBeInTheDocument()
    expect(identityApi.setUserPermissions).not.toHaveBeenCalled()
  })

  it('UsersPage_refuses_a_cap_above_what_the_server_accepts_before_calling_the_API', async () => {
    // Arrange — the server refuses more than 1000; saying so here beats a 400 after the click.
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    signedInAsAdmin()
    renderWithProviders(<UsersPage />)

    // Act
    await user.selectOptions(await screen.findByLabelText('Open requests'), 'capped')
    await user.type(screen.getByLabelText('Requests'), '1001')
    await user.click(screen.getByRole('button', { name: 'Apply' }))

    // Assert
    expect(await screen.findByText(/whole number from 1 to 1000/)).toBeInTheDocument()
    expect(identityApi.setUserPermissions).not.toHaveBeenCalled()
  })

  it('UsersPage_offers_no_cap_to_an_account_that_may_not_request_at_all', async () => {
    // Arrange — a number with nothing to count.
    vi.mocked(identityApi.users).mockResolvedValue([
      aMember({ permissions: { canRequest: false, requestsAutoApproved: false, openRequestLimit: null } }),
    ])
    signedInAsAdmin()

    // Act
    renderWithProviders(<UsersPage />)

    // Assert
    await screen.findByText('nadia')
    expect(screen.queryByLabelText('Open requests')).not.toBeInTheDocument()
  })
})

describe('UsersPage', () => {
  it('UsersPage_role_select_posts_the_chosen_role_through_create_user', async () => {
    // Arrange
    const user = userEvent.setup()
    renderWithProviders(<UsersPage />)
    await user.click(await screen.findByRole('button', { name: 'Add user' }))
    const dialog = screen.getByRole('dialog')

    // Act — fill the form and pick Administrator instead of the default Member role.
    await user.type(within(dialog).getByLabelText('Username'), 'nadia')
    await user.type(within(dialog).getByLabelText('Password'), 'correct-horse-battery')
    await user.selectOptions(within(dialog).getByLabelText('Role'), 'Administrator')
    await user.click(within(dialog).getByRole('button', { name: 'Add user' }))

    // Assert — the role chosen in the select, not the default, is what reaches the API binding.
    await waitFor(() =>
      expect(identityApi.createUser).toHaveBeenCalledWith({
        username: 'nadia',
        password: 'correct-horse-battery',
        role: 'Administrator',
      }),
    )
    expect(identityApi.createUser).toHaveBeenCalledTimes(1)
  })

  it('UsersPage_promote_click_opens_confirmation_and_waits_for_it_before_calling_set_user_role', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    renderWithProviders(<UsersPage />)

    // Act — click the promote control; this alone must not reach the API, since promotion hands
    // over every operator surface of the installation.
    await user.click(await screen.findByRole('button', { name: 'Make administrator' }))

    const dialog = await screen.findByRole('dialog', { name: 'Promote nadia to administrator?' })
    expect(identityApi.setUserRole).not.toHaveBeenCalled()

    // Act — confirm inside the dialog.
    await user.click(within(dialog).getByRole('button', { name: 'Promote to administrator' }))

    // Assert
    await waitFor(() => expect(identityApi.setUserRole).toHaveBeenCalledWith('user-1', 'Administrator'))
    expect(identityApi.setUserRole).toHaveBeenCalledTimes(1)
  })

  it('UsersPage_disable_asks_first_and_enable_does_not', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(identityApi.setUserDisabled).mockResolvedValue(undefined)
    vi.mocked(identityApi.users).mockResolvedValue([aMember()])
    renderWithProviders(<UsersPage />)

    // Act — disabling signs the account out everywhere, so the click only asks.
    await user.click(await screen.findByRole('button', { name: 'Disable' }))
    const dialog = await screen.findByRole('dialog', { name: 'Disable nadia?' })
    expect(identityApi.setUserDisabled).not.toHaveBeenCalled()
    await user.click(within(dialog).getByRole('button', { name: 'Disable account' }))

    // Assert
    await waitFor(() => expect(identityApi.setUserDisabled).toHaveBeenCalledWith('user-1', true))
  })

  it('UsersPage_demoting_yourself_asks_first_and_then_reloads_who_is_signed_in', async () => {
    // Arrange — the signed-in administrator's own row.
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([
      aMember({ id: 'admin-1', username: 'root', role: 'Administrator', isAdministrator: true }),
    ])
    renderWithProviders(<UsersPage />)
    // The mocked hook hands every caller the same context, so this is the one the page holds.
    const auth = vi.mocked(useAuth)()

    // Act
    await user.click(await screen.findByRole('button', { name: 'Make member' }))
    const dialog = await screen.findByRole('dialog', { name: 'Give up administrator access?' })
    expect(identityApi.setUserRole).not.toHaveBeenCalled()
    await user.click(within(dialog).getByRole('button', { name: 'Make me a member' }))

    // Assert
    await waitFor(() => expect(identityApi.setUserRole).toHaveBeenCalledWith('admin-1', 'Member'))
    await waitFor(() => expect(auth.refreshUser).toHaveBeenCalledTimes(1))
  })

  it('UsersPage_a_failed_reload_after_demoting_yourself_is_not_reported_as_a_failed_demotion', async () => {
    // Arrange — the demotion lands, then /me fails on a network blip.
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([
      aMember({ id: 'admin-1', username: 'root', role: 'Administrator', isAdministrator: true }),
    ])
    const auth = vi.mocked(useAuth)()
    vi.mocked(auth.refreshUser).mockRejectedValue(new Error('network down'))
    renderWithProviders(<UsersPage />)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Make member' }))
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Give up administrator access?' })).getByRole('button', {
        name: 'Make me a member',
      }),
    )

    // Assert
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.queryByText(/network down|Could not update the account/)).not.toBeInTheDocument()
  })

  it('UsersPage_disabling_your_own_account_says_it_is_yours', async () => {
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([
      aMember({ id: 'admin-1', username: 'root', role: 'Administrator', isAdministrator: true }),
    ])
    renderWithProviders(<UsersPage />)

    await user.click(await screen.findByRole('button', { name: 'Disable' }))

    const dialog = await screen.findByRole('dialog', { name: 'Disable your own account?' })
    expect(within(dialog).getByText(/You will be signed out now/)).toBeInTheDocument()
    expect(identityApi.setUserDisabled).not.toHaveBeenCalled()
  })

  it('UsersPage_demoting_another_administrator_goes_straight_through', async () => {
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([
      aMember({ id: 'admin-2', username: 'other', role: 'Administrator', isAdministrator: true }),
    ])
    renderWithProviders(<UsersPage />)

    await user.click(await screen.findByRole('button', { name: 'Make member' }))

    await waitFor(() => expect(identityApi.setUserRole).toHaveBeenCalledWith('admin-2', 'Member'))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('UsersPage permission changes', () => {
  it('UsersPage_sends_only_the_settings_when_another_permission_changes', async () => {
    // Arrange — a ceiling set under a region the installation no longer uses. The whole permission
    // object went back, the server's own reading of the ceiling included.
    const user = userEvent.setup()
    vi.mocked(identityApi.users).mockResolvedValue([
      aMember({
        permissions: {
          canRequest: true,
          requestsAutoApproved: false,
          openRequestLimit: null,
          contentCeiling: '12',
          contentCeilingRegion: 'ES',
          contentCeilingApplies: false,
        },
      }),
    ])
    vi.mocked(identityApi.setUserPermissions).mockResolvedValue(undefined)
    vi.mocked(catalogApi.contentRatings).mockResolvedValue({ region: 'US', certificates: ['G', 'PG', 'R'] })
    signedInAsAdmin()

    // Act
    renderWithProviders(<UsersPage />)
    await user.click(await screen.findByLabelText('Can request titles'))

    // Assert — the stored ceiling goes back unchanged, and the server keeps it as it was.
    await waitFor(() =>
      expect(identityApi.setUserPermissions).toHaveBeenCalledWith('user-1', {
        canRequest: false,
        requestsAutoApproved: false,
        openRequestLimit: null,
        contentCeiling: '12',
      }),
    )
  })
})
