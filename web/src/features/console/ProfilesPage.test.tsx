import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { decisionApi } from '@/api/endpoints'
import type { AcquisitionProfile } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { ProfilesPage } from './ProfilesPage'

vi.mock('@/api/endpoints', () => ({
  decisionApi: { profiles: vi.fn(), setUpgradePolicy: vi.fn(), blocks: vi.fn(), unblock: vi.fn() },
}))

function aProfile(overrides: Partial<AcquisitionProfile> = {}): AcquisitionProfile {
  return {
    id: 'profile-1',
    name: 'HD-1080p',
    minFormatScore: 0,
    cutoffRank: 3,
    upgradesAllowed: false,
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(decisionApi.profiles).mockResolvedValue([aProfile()])
  vi.mocked(decisionApi.setUpgradePolicy).mockResolvedValue(undefined)
  vi.mocked(decisionApi.blocks).mockResolvedValue({ blocks: [], truncated: false })
  vi.mocked(decisionApi.unblock).mockResolvedValue(undefined)
})

describe('ProfilesPage', () => {
  it('ProfilesPage_confirms_before_turning_upgrades_on', async () => {
    // Arrange — turning upgrades on lets the system start replacing files already on disk, so the
    // first click must only ask.
    const user = userEvent.setup()
    renderWithProviders(<ProfilesPage />)
    await user.click(await screen.findByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog')

    // Act
    await user.click(within(dialog).getByRole('checkbox', { name: /search for upgrades/i }))
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))

    // Assert — nothing written yet, and the dialog now names the specific consequence.
    expect(decisionApi.setUpgradePolicy).not.toHaveBeenCalled()
    expect(screen.getByText(/turn on upgrades for/i)).toBeInTheDocument()
    expect(screen.getByText(/already accepted.*can now be replaced/i)).toBeInTheDocument()

    // Act — confirm.
    await user.click(screen.getByRole('button', { name: 'Turn on upgrades' }))

    // Assert
    await waitFor(() =>
      expect(decisionApi.setUpgradePolicy).toHaveBeenCalledWith('profile-1', {
        cutoffRank: 3,
        upgradesAllowed: true,
      }),
    )
    expect(decisionApi.setUpgradePolicy).toHaveBeenCalledTimes(1)
  })

  it('ProfilesPage_backing_out_of_the_upgrade_confirmation_writes_nothing', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ProfilesPage />)
    await user.click(await screen.findByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog')

    await user.click(within(dialog).getByRole('checkbox', { name: /search for upgrades/i }))
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))
    await user.click(screen.getByRole('button', { name: 'Back' }))

    expect(decisionApi.setUpgradePolicy).not.toHaveBeenCalled()
    // Back returns to the editable form inside the same dialog rather than closing it outright.
    expect(screen.getByRole('checkbox', { name: /search for upgrades/i })).toBeInTheDocument()
  })

  it('ProfilesPage_cutoff_change_posts_the_complete_body_including_the_unchanged_upgrades_flag', async () => {
    // Arrange — the endpoint takes both fields at once, so leaving upgrades off must still be sent
    // explicitly rather than omitted as "unchanged".
    const user = userEvent.setup()
    renderWithProviders(<ProfilesPage />)
    await user.click(await screen.findByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog')

    // Act
    const cutoffField = within(dialog).getByLabelText('Cutoff rank')
    await user.clear(cutoffField)
    await user.type(cutoffField, '5')
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))

    // Assert — no confirmation step, and the full shape travels.
    await waitFor(() =>
      expect(decisionApi.setUpgradePolicy).toHaveBeenCalledWith('profile-1', {
        cutoffRank: 5,
        upgradesAllowed: false,
      }),
    )
    expect(decisionApi.setUpgradePolicy).toHaveBeenCalledTimes(1)
  })

  it('ProfilesPage_turning_upgrades_off_writes_directly_without_confirmation', async () => {
    const user = userEvent.setup()
    vi.mocked(decisionApi.profiles).mockResolvedValue([aProfile({ upgradesAllowed: true })])
    renderWithProviders(<ProfilesPage />)
    await user.click(await screen.findByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog')

    await user.click(within(dialog).getByRole('checkbox', { name: /search for upgrades/i }))
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(decisionApi.setUpgradePolicy).toHaveBeenCalledWith('profile-1', {
        cutoffRank: 3,
        upgradesAllowed: false,
      }),
    )
  })

  it('ProfilesPage_shows_the_load_failure_rather_than_an_empty_list', async () => {
    vi.mocked(decisionApi.profiles).mockRejectedValue(new Error('network down'))
    renderWithProviders(<ProfilesPage />)

    expect(await screen.findByText('Profiles could not be loaded')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
  })

  it('ProfilesPage_says_so_when_an_unblock_fails_and_keeps_the_note_outside_the_list', async () => {
    // Arrange — the block is still there after the failed call, so the page must not look as if it worked.
    const user = userEvent.setup()
    vi.mocked(decisionApi.blocks).mockResolvedValue({
      blocks: [
        {
          id: 'block-1',
          releaseGuid: 'g1',
          releaseTitle: 'Bad.Release.1080p',
          reason: 'Known bad encode',
          createdAt: '2026-09-23T10:00:00Z',
        },
      ],
      truncated: true,
    })
    vi.mocked(decisionApi.unblock).mockRejectedValue(
      new ApiError(500, 'server.error', 'The server could not complete the request.'),
    )
    renderWithProviders(<ProfilesPage />)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Unblock' }))

    // Assert
    expect(await screen.findByText('The server could not complete the request.')).toBeInTheDocument()
    const note = screen.getByText('There may be more blocked releases than this list shows.')
    expect(note.closest('ul')).toBeNull()
  })
})
