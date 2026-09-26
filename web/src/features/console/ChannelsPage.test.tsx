import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { notificationsApi } from '@/api/endpoints'
import type { NotificationChannel } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { ChannelsPage } from './ChannelsPage'

vi.mock('@/api/endpoints', () => ({
  notificationsApi: {
    channels: vi.fn(),
    addChannel: vi.fn(),
    setChannelEnabled: vi.fn(),
    deleteChannel: vi.fn(),
  },
}))

function aChannel(overrides: Partial<NotificationChannel> = {}): NotificationChannel {
  return {
    id: 'channel-1',
    kind: 'Discord',
    name: 'Household Discord',
    target: 'https://discord.com/api/webhooks/abc/def',
    enabled: true,
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(notificationsApi.channels).mockResolvedValue([aChannel()])
  vi.mocked(notificationsApi.deleteChannel).mockResolvedValue(undefined)
})

describe('ChannelsPage', () => {
  it('ChannelsPage_delete_click_opens_confirmation_and_waits_for_it_before_calling_delete_channel', async () => {
    // Arrange
    const user = userEvent.setup()
    renderWithProviders(<ChannelsPage />)

    // Act — click the trash icon control; this alone must not delete anything, since a channel's
    // target cannot be recovered from the UI once gone.
    await user.click(await screen.findByRole('button', { name: 'Delete Household Discord' }))

    const dialog = await screen.findByRole('dialog', { name: 'Delete Household Discord?' })
    expect(notificationsApi.deleteChannel).not.toHaveBeenCalled()

    // Act — confirm inside the dialog.
    await user.click(within(dialog).getByRole('button', { name: 'Delete channel' }))

    // Assert
    await waitFor(() => expect(notificationsApi.deleteChannel).toHaveBeenCalledWith('channel-1'))
    expect(notificationsApi.deleteChannel).toHaveBeenCalledTimes(1)
  })

  it('ChannelsPage_says_so_when_a_toggle_fails', async () => {
    // Arrange — a failed toggle used to leave the button idle and the badge unchanged, with nothing
    // to tell it apart from a click that never registered.
    const user = userEvent.setup()
    vi.mocked(notificationsApi.setChannelEnabled).mockRejectedValue(new Error('network down'))
    renderWithProviders(<ChannelsPage />)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Disable' }))

    // Assert
    expect(await screen.findByText('network down')).toBeInTheDocument()
  })
})
