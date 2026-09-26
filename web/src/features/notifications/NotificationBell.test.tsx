import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, notificationsApi } from '@/api/endpoints'
import type { AppNotification } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { NotificationBell } from './NotificationBell'

vi.mock('@/api/endpoints', () => ({
  catalogApi: { list: vi.fn() },
  notificationsApi: {
    list: vi.fn(),
    unreadCount: vi.fn(),
    markRead: vi.fn(),
    markAllRead: vi.fn(),
  },
}))

vi.mock('@/realtime/useRealtimeStatus', () => ({ useFallbackRefetchInterval: () => false }))

function aNotification(overrides: Partial<AppNotification> = {}): AppNotification {
  return {
    id: 'notification-1',
    type: 'import.completed',
    severity: 'Info',
    title: 'Arrival was imported',
    body: 'It is ready to play.',
    workId: null,
    read: false,
    createdAt: '2026-08-30T10:00:00Z',
    ...overrides,
  }
}

/** Opens the panel the way a user does — the bell is the only way in. */
async function openPanel(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /Notifications/ }))
}

beforeEach(() => {
  vi.mocked(notificationsApi.unreadCount).mockResolvedValue({ count: 0 })
  vi.mocked(catalogApi.list).mockResolvedValue([])
})

describe('NotificationBell', () => {
  it('NotificationBell_reports_a_failed_inbox_read_instead_of_claiming_it_is_empty', async () => {
    // Arrange — the inbox cannot be read. Saying "you're all caught up" here would be a false
    // all-clear at exactly the moment the household most needs to know something is wrong.
    const user = userEvent.setup()
    vi.mocked(notificationsApi.list).mockRejectedValue(
      new ApiError(503, 'notifications.unavailable', 'Notifications are temporarily unavailable.'),
    )
    renderWithProviders(<NotificationBell />)

    // Act
    await openPanel(user)

    // Assert
    expect(await screen.findByText('Notifications could not be loaded')).toBeInTheDocument()
    expect(screen.getByText('Notifications are temporarily unavailable.')).toBeInTheDocument()
    expect(screen.queryByText('No notifications')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('NotificationBell_still_shows_the_empty_state_when_the_inbox_is_genuinely_empty', async () => {
    // Arrange — the contrast case for the test above: a successful read of nothing.
    const user = userEvent.setup()
    vi.mocked(notificationsApi.list).mockResolvedValue([])
    renderWithProviders(<NotificationBell />)

    // Act
    await openPanel(user)

    // Assert
    expect(await screen.findByText('No notifications')).toBeInTheDocument()
    expect(screen.queryByText('Notifications could not be loaded')).not.toBeInTheDocument()
  })

  it('NotificationBell_explains_a_refused_mark_all_read_instead_of_leaving_the_rows_unread', async () => {
    // Arrange — one unread notification, so the bulk control is offered.
    const user = userEvent.setup()
    vi.mocked(notificationsApi.list).mockResolvedValue([aNotification()])
    vi.mocked(notificationsApi.markAllRead).mockRejectedValue(
      new ApiError(500, 'notifications.write_failed', 'The inbox could not be updated.'),
    )
    renderWithProviders(<NotificationBell />)
    await openPanel(user)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Mark all read' }))

    // Assert — the rows stay unread, and now the panel says why.
    expect(await screen.findByText('The inbox could not be updated.')).toBeInTheDocument()
  })
})
