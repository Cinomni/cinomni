import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { identityApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import { ApiError } from '@/lib/api'
import { aCurrentUser, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { AccountPage } from './AccountPage'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))
vi.mock('@/api/endpoints', () => ({
  identityApi: {
    enrollTwoFactor: vi.fn(),
    confirmTwoFactor: vi.fn(),
    disableTwoFactor: vi.fn(),
  },
}))

function signedIn(twoFactorEnabled: boolean, refreshUser = vi.fn()) {
  const value = anAuthContext(aCurrentUser({ twoFactorEnabled }), { refreshUser })
  vi.mocked(useAuth).mockReturnValue(value)
  return value
}

/** Walks the enrolment from the button to the confirmation step. */
async function startEnrolment(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Set up' }))
  await user.type(await screen.findByLabelText('Password'), 'correct horse')
  await user.click(screen.getByRole('button', { name: 'Continue' }))
}

beforeEach(() => {
  vi.mocked(identityApi.enrollTwoFactor).mockResolvedValue({
    secret: 'JBSWY3DPEHPK3PXP',
    enrollmentUri: 'otpauth://totp/Cinomni:someone?secret=JBSWY3DPEHPK3PXP&issuer=Cinomni',
  })
  vi.mocked(identityApi.confirmTwoFactor).mockResolvedValue({
    recoveryCodes: ['aaaa-1111', 'bbbb-2222', 'cccc-3333'],
  })
  vi.mocked(identityApi.disableTwoFactor).mockResolvedValue(undefined)
})

describe('AccountPage request limit', () => {
  it('AccountPage_names_the_cap_when_this_account_has_one_of_its_own', async () => {
    // Arrange — the useful case: knowing before you hit it, rather than discovering it in a refusal.
    vi.mocked(useAuth).mockReturnValue(
      anAuthContext(aCurrentUser({ permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: 3 } })),
    )

    // Act
    renderWithProviders(<AccountPage />)

    // Assert
    expect(await screen.findByText(/You can have 3 requests open at once/)).toBeInTheDocument()
  })

  it('AccountPage_does_not_invent_a_number_when_the_account_defers_to_the_installation', async () => {
    // Arrange — null means the installation's own setting applies, and `/me` does not send what that
    // setting is. Saying which case you are in is honest; naming a figure would not be.
    vi.mocked(useAuth).mockReturnValue(
      anAuthContext(aCurrentUser({ permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null } })),
    )

    // Act
    renderWithProviders(<AccountPage />)

    // Assert
    expect(await screen.findByText(/follows this installation’s own setting/)).toBeInTheDocument()
  })

  it('AccountPage_says_there_is_no_cap_when_the_account_overrides_the_default', async () => {
    // Arrange — zero is an override, not an absence: this account is uncapped whatever the
    // installation says.
    vi.mocked(useAuth).mockReturnValue(
      anAuthContext(aCurrentUser({ permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: 0 } })),
    )

    // Act
    renderWithProviders(<AccountPage />)

    // Assert
    expect(await screen.findByText(/no cap on how many requests/)).toBeInTheDocument()
  })

  it('AccountPage_says_nothing_about_limits_to_an_account_that_may_not_request', async () => {
    // Arrange — a number with nothing to count.
    vi.mocked(useAuth).mockReturnValue(
      anAuthContext(aCurrentUser({ permissions: { canRequest: false, requestsAutoApproved: false, openRequestLimit: 3 } })),
    )

    // Act
    renderWithProviders(<AccountPage />)

    // Assert
    await screen.findByText('Two-factor authentication')
    expect(screen.queryByText(/requests open at once/)).not.toBeInTheDocument()
  })
})

describe('AccountPage two-factor enrolment', () => {
  it('AccountPage_does_not_turn_the_factor_on_until_a_code_is_confirmed', async () => {
    // Arrange — the password step only starts an enrolment. The account still signs in as it did,
    // and saying otherwise here would leave someone believing they are protected when they are not.
    const user = userEvent.setup()
    signedIn(false)
    renderWithProviders(<AccountPage />)

    // Act
    await startEnrolment(user)

    // Assert
    expect(identityApi.enrollTwoFactor).toHaveBeenCalledWith('correct horse')
    expect(identityApi.confirmTwoFactor).not.toHaveBeenCalled()
    expect(await screen.findByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument()
    expect(screen.getByText(/Nothing changes\s+until you do/)).toBeInTheDocument()
  })

  it('AccountPage_shows_the_recovery_codes_once_and_refuses_to_leave_until_they_are_kept', async () => {
    // Arrange — the server keeps only hashes, so this render is the single chance to save them.
    const user = userEvent.setup()
    signedIn(false)
    renderWithProviders(<AccountPage />)
    await startEnrolment(user)

    // Act
    await user.type(await screen.findByLabelText('Code from your app'), '123456')
    await user.click(screen.getByRole('button', { name: 'Turn on' }))

    // Assert — the codes, and a Done that stays shut until the acknowledgement is ticked.
    expect(await screen.findByText('aaaa-1111')).toBeInTheDocument()
    const done = screen.getByRole('button', { name: 'Done' })
    expect(done).toBeDisabled()
    await user.click(screen.getByLabelText('I have saved these codes somewhere safe'))
    expect(done).toBeEnabled()
  })

  it('AccountPage_says_recovery_codes_also_cover_the_server_losing_its_key', async () => {
    // Arrange — "in case you lose your phone" is the obvious copy and it is incomplete: the TOTP
    // secret is encrypted with the server's master key, so losing that key makes every generated
    // code unverifiable. Only the recovery codes, which are merely hashed, still work.
    const user = userEvent.setup()
    signedIn(false)
    renderWithProviders(<AccountPage />)
    await startEnrolment(user)
    await user.type(await screen.findByLabelText('Code from your app'), '123456')
    await user.click(screen.getByRole('button', { name: 'Turn on' }))

    // Assert
    expect(await screen.findByText(/only way in if\s+this server loses its master key/)).toBeInTheDocument()
  })

  it('AccountPage_says_so_when_the_browser_refuses_to_copy_the_recovery_codes', async () => {
    // Arrange — `navigator.clipboard` does not exist outside a secure context, and a self-hosted
    // install reached over plain HTTP on a LAN is exactly that. A Copy button that silently did
    // nothing would be worst here of anywhere: these codes have no second showing.
    const user = userEvent.setup()
    signedIn(false)
    const clipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard')
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true })
    try {
      renderWithProviders(<AccountPage />)
      await startEnrolment(user)
      await user.type(await screen.findByLabelText('Code from your app'), '123456')
      await user.click(screen.getByRole('button', { name: 'Turn on' }))

      // Act
      await user.click(await screen.findByRole('button', { name: 'Copy codes' }))

      // Assert — and it never claims it copied.
      expect(await screen.findByText(/Download\s+the file instead/)).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Copied' })).not.toBeInTheDocument()
    } finally {
      if (clipboard) Object.defineProperty(navigator, 'clipboard', clipboard)
    }
  })

  it('AccountPage_does_not_repeat_the_sign_in_wording_for_a_password_it_already_knows_the_field_of', async () => {
    // The API answers 401 here with "Invalid username or password", which is the sign-in sentence:
    // it names a field this screen has not got and a step the person is already past. Saying what
    // was actually refused is the caller's job, and only for this status.
    const user = userEvent.setup()
    signedIn(false)
    vi.mocked(identityApi.enrollTwoFactor).mockRejectedValue(
      new ApiError(401, 'identity.invalid_credentials', 'Invalid username or password.'),
    )
    renderWithProviders(<AccountPage />)

    // Act
    await startEnrolment(user)

    // Assert
    expect(await screen.findByText('That password was not accepted.')).toBeInTheDocument()
    expect(screen.queryByText(/Invalid username/)).not.toBeInTheDocument()
  })

  it('AccountPage_re_reads_the_account_once_the_factor_is_actually_on', async () => {
    // Arrange — /me is read from the database rather than the session claims precisely so it can
    // change during the session that changed it.
    const user = userEvent.setup()
    const refreshUser = vi.fn().mockResolvedValue(undefined)
    signedIn(false, refreshUser)
    renderWithProviders(<AccountPage />)
    await startEnrolment(user)

    // Act
    await user.type(await screen.findByLabelText('Code from your app'), '123456')
    await user.click(screen.getByRole('button', { name: 'Turn on' }))

    // Assert
    await waitFor(() => expect(refreshUser).toHaveBeenCalled())
  })

  it('AccountPage_keeps_the_recovery_codes_up_once_the_account_reports_the_factor_on', async () => {
    // Arrange — confirming re-reads the account while the codes are showing, and the re-read says the
    // factor is now on. The codes are never shown again, so that must not be what closes them.
    const user = userEvent.setup()
    const refreshUser = vi.fn().mockImplementation(async () => {
      signedIn(true, refreshUser)
    })
    signedIn(false, refreshUser)
    const { rerender } = renderWithProviders(<AccountPage />)
    await startEnrolment(user)

    // Act
    await user.type(await screen.findByLabelText('Code from your app'), '123456')
    await user.click(screen.getByRole('button', { name: 'Turn on' }))
    await waitFor(() => expect(refreshUser).toHaveBeenCalled())
    rerender(<AccountPage />)

    // Assert — still up, and Done is still what closes them.
    expect(await screen.findByText('aaaa-1111')).toBeInTheDocument()
    await user.click(screen.getByLabelText('I have saved these codes somewhere safe'))
    await user.click(screen.getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(screen.queryByText('aaaa-1111')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Turn off' })).toBeInTheDocument()
  })

  it('AccountPage_starts_over_with_the_password_when_too_many_wrong_codes_cancelled_the_setup', async () => {
    // Arrange — past five wrong codes the server drops the pending secret, so the key on screen is
    // dead. Asking for yet another code would be a loop; the only way on is the password again.
    const user = userEvent.setup()
    signedIn(false)
    vi.mocked(identityApi.confirmTwoFactor).mockRejectedValue(
      new ApiError(
        400,
        'identity.two_factor_enrollment_abandoned',
        'Too many wrong codes, so this setup was cancelled. Start again with your password.',
      ),
    )
    renderWithProviders(<AccountPage />)
    await startEnrolment(user)
    await user.type(await screen.findByLabelText('Code from your app'), '000000')
    await user.click(screen.getByRole('button', { name: 'Turn on' }))

    // Act
    expect(await screen.findByText(/Too many wrong codes/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Start again' }))

    // Assert
    expect(await screen.findByLabelText('Password')).toBeInTheDocument()
    expect(screen.queryByText('JBSWY3DPEHPK3PXP')).not.toBeInTheDocument()
  })

  it('AccountPage_blames_the_server_not_the_password_when_the_installation_has_no_master_key', async () => {
    // Arrange — 503 two_factor_unavailable is the one failure here that no retry and no correction
    // by this person can fix. Reporting it as a bad password sends them round a loop they cannot win.
    const user = userEvent.setup()
    signedIn(false)
    vi.mocked(identityApi.enrollTwoFactor).mockRejectedValue(
      new ApiError(503, 'identity.two_factor_unavailable', 'No master key.'),
    )
    renderWithProviders(<AccountPage />)

    // Act
    await startEnrolment(user)

    // Assert
    expect(await screen.findByText(/no master key configured/)).toBeInTheDocument()
    expect(screen.queryByText('That password was not accepted.')).not.toBeInTheDocument()
  })
})

describe('AccountPage two-factor removal', () => {
  it('AccountPage_warns_that_turning_it_off_signs_the_account_out_everywhere_else', async () => {
    // Arrange — the API revokes every other session. Someone who is not told will find themselves
    // signed out on their other devices with no idea why.
    const user = userEvent.setup()
    signedIn(true)
    renderWithProviders(<AccountPage />)

    // Act
    await user.click(screen.getByRole('button', { name: 'Turn off' }))

    // Assert
    expect(await screen.findByText(/also signs you out everywhere else/i)).toBeInTheDocument()
  })

  it('AccountPage_sends_both_the_password_and_the_code_to_turn_it_off', async () => {
    // Arrange
    const user = userEvent.setup()
    const refreshUser = vi.fn().mockResolvedValue(undefined)
    signedIn(true, refreshUser)
    renderWithProviders(<AccountPage />)
    await user.click(screen.getByRole('button', { name: 'Turn off' }))

    // Act — scoped to the dialog: the card behind it carries a "Turn off" button of its own.
    const dialog = within(await screen.findByRole('dialog'))
    await user.type(dialog.getByLabelText('Password'), 'correct horse')
    await user.type(dialog.getByLabelText('Authentication code'), 'aaaa-1111')
    await user.click(dialog.getByRole('button', { name: 'Turn off' }))

    // Assert — a recovery code is accepted here too, so it is passed through untouched.
    await waitFor(() =>
      expect(identityApi.disableTwoFactor).toHaveBeenCalledWith('correct horse', 'aaaa-1111'),
    )
  })
})
