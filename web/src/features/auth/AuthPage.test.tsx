import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useAuth } from '@/auth/useAuth'
import type { AuthContextValue, LoginOutcome } from '@/auth/context'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { AuthPage } from './AuthPage'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))

/** A signed-out context whose callbacks are spies the test drives. */
function anonymousAuth(overrides: Partial<AuthContextValue> = {}): AuthContextValue {
  const value: AuthContextValue = {
    status: 'anon',
    user: null,
    login: vi.fn(),
    completeTwoFactor: vi.fn(),
    setup: vi.fn(),
    logout: vi.fn(),
    refreshUser: vi.fn(),
    retrySession: vi.fn(),
    ...overrides,
  }
  vi.mocked(useAuth).mockReturnValue(value)
  return value
}

const NO_SECOND_FACTOR: LoginOutcome = { twoFactorRequired: false }

function challengeIn(seconds: number): LoginOutcome {
  return {
    twoFactorRequired: true,
    challenge: 'challenge-1',
    expiresInSeconds: seconds,
  }
}

async function signIn(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Username'), 'someone')
  await user.type(screen.getByLabelText('Password'), 'correct horse')
  await user.click(screen.getByRole('button', { name: 'Sign in' }))
}

beforeEach(() => {
  vi.useRealTimers()
})

describe('AuthPage', () => {
  it('AuthPage_signs_in_directly_when_the_account_keeps_no_second_factor', async () => {
    // Arrange
    const user = userEvent.setup()
    const auth = anonymousAuth({ login: vi.fn().mockResolvedValue(NO_SECOND_FACTOR) })
    renderWithProviders(<AuthPage mode="login" />)

    expect(screen.getByRole('link', { name: 'Source code (AGPLv3+)' })).toHaveAttribute(
      'href',
      'https://github.com/Cinomni/cinomni',
    )

    // Act
    await signIn(user)

    // Assert — no second step is invented for an account that does not have one.
    expect(auth.login).toHaveBeenCalledWith('someone', 'correct horse')
    expect(screen.queryByLabelText('Authentication code')).not.toBeInTheDocument()
  })

  it('AuthPage_asks_for_a_code_when_the_password_only_bought_a_challenge', async () => {
    // Arrange — a correct password on an account with a second factor is a 200, not a failure.
    const user = userEvent.setup()
    anonymousAuth({ login: vi.fn().mockResolvedValue(challengeIn(300)) })
    renderWithProviders(<AuthPage mode="login" />)

    // Act
    await signIn(user)

    // Assert
    expect(await screen.findByLabelText('Authentication code')).toBeInTheDocument()
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument()
  })

  it('AuthPage_redeems_the_challenge_with_whatever_code_was_typed', async () => {
    // Arrange — a recovery code is not six digits, and goes through the same field. Anything that
    // narrowed the input would close off the way back in for someone without their authenticator.
    const user = userEvent.setup()
    const auth = anonymousAuth({
      login: vi.fn().mockResolvedValue(challengeIn(300)),
      completeTwoFactor: vi.fn().mockResolvedValue(undefined),
    })
    renderWithProviders(<AuthPage mode="login" />)
    await signIn(user)

    // Act
    await user.type(await screen.findByLabelText('Authentication code'), 'abcd-efgh-jkmn')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    // Assert
    expect(auth.completeTwoFactor).toHaveBeenCalledWith('challenge-1', 'abcd-efgh-jkmn')
  })

  it('AuthPage_states_the_limits_it_cannot_diagnose_after_a_rejected_code', async () => {
    // Arrange — every cause answers with one error on purpose (expired, spent, wrong, exhausted), so
    // the screen cannot say which. It says so up front instead, or the user retypes the same code
    // against a challenge that is already dead.
    const user = userEvent.setup()
    anonymousAuth({
      login: vi.fn().mockResolvedValue(challengeIn(300)),
      completeTwoFactor: vi
        .fn()
        .mockRejectedValue(new ApiError(401, 'identity.invalid_two_factor_code', 'Invalid.')),
    })
    renderWithProviders(<AuthPage mode="login" />)
    await signIn(user)

    // Act
    await user.type(await screen.findByLabelText('Authentication code'), '000000')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    // Assert — the failure, plus the standing explanation of what kills a challenge.
    expect(await screen.findByText('That code was not accepted.')).toBeInTheDocument()
    expect(screen.getByText(/discarded after several\s+incorrect codes/)).toBeInTheDocument()
  })

  it('AuthPage_sends_the_user_back_to_the_password_when_the_challenge_runs_out', async () => {
    // Arrange — a challenge with no time left. The countdown never blocks a submission; at zero it
    // does the one thing it can be sure about, which is returning to a step that definitely works.
    const user = userEvent.setup()
    anonymousAuth({ login: vi.fn().mockResolvedValue(challengeIn(0)) })
    renderWithProviders(<AuthPage mode="login" />)

    // Act
    await signIn(user)

    // Assert
    expect(
      await screen.findByText('That sign-in request expired. Enter your password again.', undefined, {
        timeout: 3000,
      }),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
  })

  it('AuthPage_counts_the_challenge_from_its_arrival_whatever_the_browser_clock_says', async () => {
    // Arrange — this browser runs ten minutes ahead of the server. Measured against the server's
    // expiresAt, a five-minute challenge would already be over; measured as a lifetime, it has five
    // minutes left.
    const user = userEvent.setup()
    const aheadOfServer = Date.now() + 10 * 60 * 1000
    vi.spyOn(Date, 'now').mockReturnValue(aheadOfServer)
    anonymousAuth({ login: vi.fn().mockResolvedValue(challengeIn(300)) })
    renderWithProviders(<AuthPage mode="login" />)

    // Act
    await signIn(user)

    // Assert
    expect(await screen.findByText(/expires in 5:00/)).toBeInTheDocument()
    expect(screen.getByLabelText('Authentication code')).toBeInTheDocument()
    vi.mocked(Date.now).mockRestore()
  })

  it('AuthPage_lets_the_user_abandon_the_second_step', async () => {
    // Arrange
    const user = userEvent.setup()
    anonymousAuth({ login: vi.fn().mockResolvedValue(challengeIn(300)) })
    renderWithProviders(<AuthPage mode="login" />)
    await signIn(user)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Back to sign in' }))

    // Assert
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
    expect(screen.queryByLabelText('Authentication code')).not.toBeInTheDocument()
  })
})
