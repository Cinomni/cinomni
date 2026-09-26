import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { monitoringApi } from '@/api/endpoints'
import type { MonitoredTarget } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { aTarget } from '@/test/factories'
import { WantedPage } from './WantedPage'

vi.mock('@/api/endpoints', () => ({
  monitoringApi: { missing: vi.fn() },
}))

function targets(count: number, overrides: Partial<MonitoredTarget> = {}): MonitoredTarget[] {
  return Array.from({ length: count }, (_, index) =>
    aTarget({ id: `target-${index}`, monitored: true, isMissing: true, ...overrides }),
  )
}

describe('WantedPage', () => {
  it('WantedPage_warns_there_may_be_more_when_the_page_comes_back_exactly_full', async () => {
    // Arrange — the default row limit is 100; a full page is the only honest signal of a truncation,
    // since the endpoint has no total count and no offset paging.
    vi.mocked(monitoringApi.missing).mockResolvedValue(targets(100))

    // Act
    renderWithProviders(<WantedPage />)

    // Assert
    expect(await screen.findByText(/there may be more/i)).toBeInTheDocument()
    expect(monitoringApi.missing).toHaveBeenCalledWith(100)
  })

  it('WantedPage_stays_quiet_when_the_page_comes_back_short_of_the_limit', async () => {
    // Arrange — fewer rows than the requested limit means this is the whole wanted list.
    vi.mocked(monitoringApi.missing).mockResolvedValue(targets(3))

    // Act
    renderWithProviders(<WantedPage />)

    // Assert
    expect(await screen.findAllByText('Missing')).toHaveLength(3)
    expect(screen.queryByText(/there may be more/i)).not.toBeInTheDocument()
  })

  it('WantedPage_marks_an_unaired_episode_differently_from_a_title_that_should_already_be_on_disk', async () => {
    // Arrange
    const future = new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString().slice(0, 10)
    vi.mocked(monitoringApi.missing).mockResolvedValue([
      aTarget({ id: 'aired', kind: 'Episode', monitored: true, isMissing: true, seasonNumber: 1, episodeNumber: 1, airDate: '2020-01-01' }),
      aTarget({ id: 'unaired', kind: 'Episode', monitored: true, isMissing: true, seasonNumber: 1, episodeNumber: 2, airDate: future }),
    ])

    // Act
    renderWithProviders(<WantedPage />)

    // Assert
    expect(await screen.findByText('Missing')).toBeInTheDocument()
    expect(screen.getByText('Unaired')).toBeInTheDocument()
  })

  it('WantedPage_shows_a_retry_control_when_the_request_fails', async () => {
    // Arrange
    vi.mocked(monitoringApi.missing).mockRejectedValue(new Error('network down'))

    // Act
    renderWithProviders(<WantedPage />)

    // Assert
    expect(await screen.findByText('network down')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('WantedPage_reports_when_nothing_is_wanted', async () => {
    // Arrange
    vi.mocked(monitoringApi.missing).mockResolvedValue([])

    // Act
    renderWithProviders(<WantedPage />)

    // Assert
    expect(await screen.findByText('Nothing wanted')).toBeInTheDocument()
  })
})
