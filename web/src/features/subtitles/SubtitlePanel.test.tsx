import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { subtitlesApi } from '@/api/endpoints'
import type { SubtitleSearchSummary } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { SubtitlePanel } from './SubtitlePanel'

vi.mock('@/api/endpoints', () => ({
  subtitlesApi: { forAsset: vi.fn() },
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
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(subtitlesApi.forAsset).mockReset()
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
})
