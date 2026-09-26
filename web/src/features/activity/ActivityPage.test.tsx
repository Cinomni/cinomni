import { describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import { acquisitionApi, catalogApi, downloadsApi } from '@/api/endpoints'
import type { AcquisitionIntentSummary, DownloadTaskSummary, IntentState } from '@/api/types'
import { aWork } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { ActivityPage } from './ActivityPage'

vi.mock('@/api/endpoints', () => ({
  acquisitionApi: { intents: vi.fn(), intent: vi.fn() },
  catalogApi: { list: vi.fn() },
  monitoringApi: { targets: vi.fn().mockResolvedValue([]) },
  downloadsApi: { list: vi.fn().mockResolvedValue([]), tunnel: vi.fn().mockRejectedValue(new Error('not needed')) },
}))

function aTransfer(overrides: Partial<DownloadTaskSummary> = {}): DownloadTaskSummary {
  return {
    id: 'task-1',
    infoHash: 'abc',
    name: 'Arrival.2016.2160p.WEB-DL',
    state: 'Downloading',
    progress: 0.4,
    downloadRate: 2 * 1024 * 1024,
    uploadRate: 512 * 1024,
    numPeers: 30,
    numSeeds: 12,
    networkHeld: false,
    intentIds: [],
    ...overrides,
  }
}

function anIntent(id: string, workId: string, state: IntentState): AcquisitionIntentSummary {
  return { id, targetId: `t-${id}`, workId, state, attemptCount: 1, maxAttempts: 3, selectedReleaseGuid: null }
}

describe('ActivityPage', () => {
  it('ActivityPage_puts_what_needs_a_person_first_and_names_each_stage_in_words', async () => {
    // Arrange
    vi.mocked(catalogApi.list).mockResolvedValue([
      aWork({ id: 'w1', title: 'Arrival' }),
      aWork({ id: 'w2', title: 'Dune' }),
      aWork({ id: 'w3', title: 'Heat' }),
    ])
    vi.mocked(acquisitionApi.intents).mockResolvedValue([
      anIntent('i1', 'w1', 'Downloading'),
      anIntent('i2', 'w2', 'Exhausted'),
      anIntent('i3', 'w3', 'Available'),
    ])

    // Act
    renderWithProviders(<ActivityPage />)

    // Assert — the exhausted title leads, explained, and the pipeline says where it stopped.
    const headings = await screen.findAllByRole('heading', { level: 2 })
    expect(headings.map((heading) => heading.textContent)).toEqual(['Needs attention', 'In progress', 'Done'])
    const attention = screen.getByRole('region', { name: 'Needs attention' })
    expect(within(attention).getByText('Dune')).toBeInTheDocument()
    expect(within(attention).getByText('No acceptable release found')).toBeInTheDocument()
    expect(within(attention).getByText('Stopped at Search')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'In progress' })).getByText('Download in progress')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Done' })).getByText('All stages complete')).toBeInTheDocument()
  })

  it('ActivityPage_shows_a_transfer_under_the_title_it_serves_with_its_live_figures', async () => {
    // Arrange
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Arrival' })])
    vi.mocked(acquisitionApi.intents).mockResolvedValue([anIntent('i1', 'w1', 'Downloading')])
    vi.mocked(downloadsApi.list).mockResolvedValue([aTransfer({ intentIds: ['i1'] })])

    // Act
    renderWithProviders(<ActivityPage />)

    // Assert — the torrent sits inside the title's row, not in a separate list.
    const transfers = within(await screen.findByRole('list', { name: 'Transfers for Arrival' }))
    expect(transfers.getByText('Arrival.2016.2160p.WEB-DL')).toBeInTheDocument()
    expect(transfers.getByText('40%')).toBeInTheDocument()
    expect(transfers.getByText('↓ 2.0 MB/s')).toBeInTheDocument()
    expect(transfers.getByText('↑ 512 KB/s')).toBeInTheDocument()
    expect(transfers.getByText('12 · 30')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Other transfers' })).not.toBeInTheDocument()
  })

  it('ActivityPage_says_so_when_a_downloading_title_has_no_torrent_behind_it', async () => {
    // Arrange — the goal still says Downloading, but its torrent was removed from the client.
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Big Buck Bunny' })])
    vi.mocked(acquisitionApi.intents).mockResolvedValue([anIntent('i1', 'w1', 'Downloading')])
    vi.mocked(downloadsApi.list).mockResolvedValue([])

    // Act
    renderWithProviders(<ActivityPage />)

    // Assert
    expect(await screen.findByText(/No torrent is running for this title/)).toBeInTheDocument()
  })

  it('ActivityPage_keeps_a_transfer_no_listed_title_claims_reachable', async () => {
    // Arrange — a torrent whose goal is not in the list must not simply vanish from the operator's view.
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Arrival' })])
    vi.mocked(acquisitionApi.intents).mockResolvedValue([anIntent('i1', 'w1', 'Searching')])
    vi.mocked(downloadsApi.list).mockResolvedValue([aTransfer({ name: 'Heat.1995.1080p', intentIds: ['gone'] })])

    // Act
    renderWithProviders(<ActivityPage />)

    // Assert
    const other = within(await screen.findByRole('region', { name: 'Other transfers' }))
    expect(other.getByText('Heat.1995.1080p')).toBeInTheDocument()
  })

  it('ActivityPage_leaves_out_a_group_with_nothing_in_it', async () => {
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Arrival' })])
    vi.mocked(acquisitionApi.intents).mockResolvedValue([anIntent('i1', 'w1', 'Searching')])

    renderWithProviders(<ActivityPage />)

    expect(await screen.findByRole('heading', { name: 'In progress' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Needs attention' })).not.toBeInTheDocument()
  })
})
