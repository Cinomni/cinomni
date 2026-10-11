import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { subtitlesApi } from '@/api/endpoints'
import type { SubtitleSearchSummary, SubtitleSync } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { SubtitlePanel } from './SubtitlePanel'

vi.mock('@/api/endpoints', () => ({
  subtitlesApi: { forAsset: vi.fn(), resync: vi.fn(), revert: vi.fn() },
}))

function aSearch(overrides: Partial<SubtitleSearchSummary> = {}): SubtitleSearchSummary {
  return {
    id: 'search-1',
    assetId: 'asset-1',
    language: 'en',
    forced: false,
    hearingImpaired: false,
    state: 'Available',
    attempts: 1,
    subtitle: null,
    ...overrides,
  }
}

function aSync(overrides: Partial<SubtitleSync> = {}): SubtitleSync {
  return {
    outcome: 'Applied',
    offsetMs: 1240,
    ratio: 1,
    score: 0.61,
    reason: 'Shifted by +1.24 s.',
    hasOriginal: true,
    decidedAt: '2026-10-11T10:00:00Z',
    ...overrides,
  }
}

function aSyncedSearch(sync: Partial<SubtitleSync> = {}, search: Partial<SubtitleSearchSummary> = {}) {
  return aSearch({ subtitle: { id: 'subtitle-1', format: 'Srt', sync: aSync(sync) }, ...search })
}

beforeEach(() => {
  vi.mocked(subtitlesApi.forAsset).mockReset()
  vi.mocked(subtitlesApi.resync).mockReset()
  vi.mocked(subtitlesApi.revert).mockReset()
})

describe('SubtitlePanel', () => {
  it('SubtitlePanel_reports_no_search_recorded_rather_than_no_subtitles_available', async () => {
    // Arrange — an asset with zero searches has never been looked at, which is not the same claim as
    // "we looked and found nothing".
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([])

    // Act
    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    // Assert
    expect(await screen.findByText('No subtitle search recorded')).toBeInTheDocument()
    expect(screen.queryByText(/no subtitles available/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('SubtitlePanel_renders_a_failed_search_distinctly_from_having_no_searches_at_all', async () => {
    // Arrange — several attempts that never found an acceptable match is a real, recorded outcome.
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([
      aSearch({ id: 'search-2', language: 'es', state: 'NotFound', attempts: 4 }),
    ])

    // Act
    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    // Assert
    expect(await screen.findByRole('table', { name: 'Subtitle searches for this file' })).toBeInTheDocument()
    expect(screen.getByText('NotFound')).toBeInTheDocument()
    expect(screen.getByText('es')).toBeInTheDocument()
    expect(screen.getByText('4')).toBeInTheDocument()
    expect(screen.queryByText('No subtitle search recorded')).not.toBeInTheDocument()
  })

  it('SubtitlePanel_shows_the_forced_and_hearing_impaired_flags_and_state_per_row', async () => {
    // Arrange
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([
      aSearch({ id: 'search-3', language: 'fr', forced: true, hearingImpaired: true, state: 'Available', attempts: 2 }),
    ])

    // Act
    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    // Assert
    await screen.findByText('fr')
    expect(screen.getAllByText('Yes')).toHaveLength(2)
    expect(screen.getByText('Available')).toBeInTheDocument()
    expect(screen.getByText('2')).toBeInTheDocument()
  })

  it('SubtitlePanel_shows_a_retryable_error_when_the_request_fails', async () => {
    // Arrange
    vi.mocked(subtitlesApi.forAsset).mockRejectedValue(new Error('network down'))

    // Act
    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    // Assert
    expect(await screen.findByText('Subtitle status could not be loaded')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('SubtitlePanel_names_an_applied_shift_with_its_offset_and_frame_rate_conversion', async () => {
    // Arrange
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([
      aSyncedSearch({ ratio: (25 * 1001) / 24000, reason: 'Shifted by +1.24 s and retimed from 25 to 23.976 fps.' }),
    ])

    // Act
    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    // Assert — the table and the timing list both name it, and the backend's reason is quoted.
    expect(await screen.findAllByText('Synced +1.24 s (25→23.976)')).toHaveLength(2)
    expect(screen.getByText('Shifted by +1.24 s and retimed from 25 to 23.976 fps.')).toBeInTheDocument()
  })

  it('SubtitlePanel_says_the_original_was_kept_when_the_match_was_not_convincing', async () => {
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([
      aSyncedSearch({ outcome: 'LowConfidence', hasOriginal: false, reason: 'Low confidence: the best match scores 0.12.' }),
    ])

    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    expect(await screen.findAllByText('Original kept: low confidence')).toHaveLength(2)
    expect(screen.getByText('Low confidence: the best match scores 0.12.')).toBeInTheDocument()
  })

  it('SubtitlePanel_offers_no_timing_actions_to_a_member', async () => {
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([aSyncedSearch()])

    renderWithProviders(<SubtitlePanel assetId="asset-1" />)

    await screen.findAllByText('Synced +1.24 s')
    expect(screen.queryByRole('button', { name: /re-sync/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /revert/i })).not.toBeInTheDocument()
  })

  it('SubtitlePanel_reverts_only_after_the_administrator_confirms_and_then_reloads', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([aSyncedSearch()])
    vi.mocked(subtitlesApi.revert).mockResolvedValue(undefined)
    renderWithProviders(<SubtitlePanel assetId="asset-1" canManage />)

    // Act — the first click only asks.
    await user.click(await screen.findByRole('button', { name: 'Revert en subtitle to the downloaded file' }))

    // Assert
    expect(subtitlesApi.revert).not.toHaveBeenCalled()
    expect(screen.getByText('Play the downloaded file again?')).toBeInTheDocument()

    // Act — confirming does it, for this subtitle, and the list is fetched again.
    await user.click(screen.getByRole('button', { name: 'Confirm revert' }))

    // Assert
    expect(subtitlesApi.revert).toHaveBeenCalledWith('subtitle-1')
    await vi.waitFor(() => expect(subtitlesApi.forAsset).toHaveBeenCalledTimes(2))
  })

  it('SubtitlePanel_resyncs_on_request_and_cannot_while_a_check_is_running', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([
      aSyncedSearch({}, { id: 'search-a' }),
      aSearch({
        id: 'search-b',
        language: 'es',
        state: 'Syncing',
        subtitle: { id: 'subtitle-2', format: 'Srt', sync: null },
      }),
    ])
    vi.mocked(subtitlesApi.resync).mockResolvedValue(undefined)
    renderWithProviders(<SubtitlePanel assetId="asset-1" canManage />)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Re-sync en subtitle' }))

    // Assert
    expect(subtitlesApi.resync).toHaveBeenCalledWith('subtitle-1')
    expect(screen.getByRole('button', { name: 'Re-sync es subtitle' })).toBeDisabled()
    expect(screen.getAllByText('Checking timing…')).toHaveLength(2)
  })

  it('SubtitlePanel_shows_why_an_action_was_refused', async () => {
    const user = userEvent.setup()
    vi.mocked(subtitlesApi.forAsset).mockResolvedValue([aSyncedSearch()])
    vi.mocked(subtitlesApi.resync).mockRejectedValue(new Error('Subtitle synchronization is turned off.'))
    renderWithProviders(<SubtitlePanel assetId="asset-1" canManage />)

    await user.click(await screen.findByRole('button', { name: 'Re-sync en subtitle' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Subtitle synchronization is turned off.')
  })
})
