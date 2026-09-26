import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, screen, waitFor } from '@testing-library/react'
import { identityApi } from '@/api/endpoints'
import { ApiError } from '@/lib/api'
import { TOKEN_STORAGE_KEY, tokenStore } from '@/lib/token'
import { aCurrentUser } from '@/test/factories'
import { renderWithProviders, testQueryClient } from '@/test/render'
import { AuthProvider } from './AuthProvider'
import { useAuth } from './useAuth'

vi.mock('@/api/endpoints', () => ({
  identityApi: {
    me: vi.fn(),
    setupRequired: vi.fn(),
    login: vi.fn(),
    loginTwoFactor: vi.fn(),
    setup: vi.fn(),
    logout: vi.fn(),
  },
}))

function Probe() {
  const { status, user } = useAuth()
  return (
    <p>
      {status}:{user?.username ?? 'nobody'}
    </p>
  )
}

/** What the browser fires in every other tab when one of them writes the token. */
function anotherTabWrites(newValue: string | null) {
  const oldValue = tokenStore.get()
  if (newValue === null) tokenStore.clear()
  else tokenStore.set(newValue)
  act(() => {
    window.dispatchEvent(new StorageEvent('storage', { key: TOKEN_STORAGE_KEY, oldValue, newValue }))
  })
}

beforeEach(() => {
  tokenStore.set('a-session-token')
  vi.mocked(identityApi.setupRequired).mockResolvedValue({ setupRequired: false })
})

afterEach(() => {
  tokenStore.clear()
})

describe('AuthProvider session bootstrap', () => {
  it('AuthProvider_keeps_the_session_when_the_server_cannot_be_reached', async () => {
    // Arrange — the server is restarting; the token is as good as it was a minute ago.
    vi.mocked(identityApi.me).mockRejectedValue(new ApiError(503, '503', 'Service Unavailable'))

    // Act
    renderWithProviders(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )

    // Assert
    expect(await screen.findByText('unavailable:nobody')).toBeInTheDocument()
    expect(tokenStore.get()).toBe('a-session-token')
  })

  it('AuthProvider_keeps_the_session_through_a_network_failure', async () => {
    vi.mocked(identityApi.me).mockRejectedValue(new TypeError('Failed to fetch'))

    renderWithProviders(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )

    expect(await screen.findByText('unavailable:nobody')).toBeInTheDocument()
    expect(tokenStore.get()).toBe('a-session-token')
  })

  it('AuthProvider_signs_out_when_the_server_refuses_the_token', async () => {
    vi.mocked(identityApi.me).mockRejectedValue(new ApiError(401, '401', 'Unauthorized'))

    renderWithProviders(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )

    expect(await screen.findByText('anon:nobody')).toBeInTheDocument()
    expect(tokenStore.get()).toBeNull()
  })
})

describe('AuthProvider across tabs', () => {
  it('AuthProvider_follows_another_tab_signing_in_as_someone_else_and_drops_the_old_cache', async () => {
    // Arrange — this tab shows one account; another tab signs in as a different one.
    vi.mocked(identityApi.me).mockResolvedValueOnce(aCurrentUser({ id: 'user-1', username: 'nadia' }))
    const queryClient = testQueryClient()
    renderWithProviders(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
      queryClient,
    )
    expect(await screen.findByText('authed:nadia')).toBeInTheDocument()
    queryClient.setQueryData(['requests'], ['nadia-only'])
    vi.mocked(identityApi.me).mockResolvedValueOnce(aCurrentUser({ id: 'user-2', username: 'omar' }))

    // Act
    anotherTabWrites('omars-token')

    // Assert
    expect(await screen.findByText('authed:omar')).toBeInTheDocument()
    expect(queryClient.getQueryData(['requests'])).toBeUndefined()
  })

  it('AuthProvider_follows_another_tab_signing_out', async () => {
    vi.mocked(identityApi.me).mockResolvedValueOnce(aCurrentUser({ username: 'nadia' }))
    renderWithProviders(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )
    expect(await screen.findByText('authed:nadia')).toBeInTheDocument()

    anotherTabWrites(null)

    await waitFor(() => expect(screen.getByText('anon:nobody')).toBeInTheDocument())
  })
})

describe('AuthProvider stale answers', () => {
  it('AuthProvider_drops_a_me_answer_that_arrives_after_another_tab_moved_the_session_on', async () => {
    // Arrange — the first /me is slow; another tab signs in as someone else before it answers.
    let answerFirst: (user: ReturnType<typeof aCurrentUser>) => void = () => undefined
    vi.mocked(identityApi.me)
      .mockReturnValueOnce(new Promise((resolve) => (answerFirst = resolve)))
      .mockResolvedValueOnce(aCurrentUser({ id: 'user-2', username: 'omar' }))
    renderWithProviders(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    )
    anotherTabWrites('omars-token')
    expect(await screen.findByText('authed:omar')).toBeInTheDocument()

    // Act — the old answer lands late.
    await act(async () => answerFirst(aCurrentUser({ id: 'user-1', username: 'nadia' })))

    // Assert — the tab still shows the account whose token it now sends.
    expect(screen.getByText('authed:omar')).toBeInTheDocument()
  })
})
