import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { monitoringApi } from '@/api/endpoints'
import type { CalendarEntry } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { CalendarPage } from './CalendarPage'

vi.mock('@/api/endpoints', () => ({
  monitoringApi: { calendar: vi.fn() },
  // Only feeds the artwork beside each airing.
  catalogApi: { list: vi.fn().mockResolvedValue([]) },
}))

function entry(overrides: Partial<CalendarEntry> = {}): CalendarEntry {
  return {
    id: 'target-1',
    workId: 'work-1',
    workTitle: 'The Wire',
    kind: 'Episode',
    seasonNumber: 1,
    episodeNumber: 1,
    title: 'The Target',
    airDate: '2026-09-22',
    airDateTime: null,
    monitored: true,
    isMissing: true,
    ...overrides,
  }
}

describe('CalendarPage', () => {
  it('CalendarPage_groups_an_airing_under_its_published_date', async () => {
    vi.mocked(monitoringApi.calendar).mockResolvedValue([entry()])

    renderWithProviders(<CalendarPage />)

    expect(await screen.findByText('The Wire')).toBeInTheDocument()
    expect(screen.getByText(/S01E01/)).toBeInTheDocument()
    expect(screen.getByText('Missing')).toBeInTheDocument()
  })

  it('CalendarPage_says_when_nothing_is_airing', async () => {
    vi.mocked(monitoringApi.calendar).mockResolvedValue([])

    renderWithProviders(<CalendarPage />)

    expect(await screen.findByText('Nothing airing')).toBeInTheDocument()
  })
})
