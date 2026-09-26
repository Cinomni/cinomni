import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { metadataApi } from '@/api/endpoints'
import type { MetadataArtwork, MetadataSnapshot } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { ArtworkPicker } from './ArtworkPicker'

vi.mock('@/api/endpoints', () => ({
  metadataApi: { snapshot: vi.fn(), selectArtwork: vi.fn() },
}))

function anArtwork(overrides: Partial<MetadataArtwork> = {}): MetadataArtwork {
  return {
    id: 'artwork-1',
    kind: 'Poster',
    url: 'https://images.example/poster.jpg',
    thumbnailUrl: 'https://images.example/poster.jpg',
    language: 'en',
    width: 1000,
    height: 1500,
    voteAverage: 7.5,
    voteCount: 40,
    isSelected: false,
    ...overrides,
  }
}

function aSnapshot(overrides: Partial<MetadataSnapshot> = {}): MetadataSnapshot {
  return {
    id: 'snapshot-1',
    workId: 'work-1',
    provider: 'TMDB',
    kind: 'Movie',
    externalId: '329865',
    title: 'Arrival',
    originalTitle: null,
    year: 2016,
    overview: null,
    runtimeMinutes: 116,
    originalLanguage: 'en',
    posterUrl: null,
    backdropUrl: null,
    fetchedAt: '2026-08-30T10:00:00Z',
    artwork: [anArtwork()],
    ...overrides,
  }
}

const NO_CANDIDATES_COPY = /No artwork candidates were found/

beforeEach(() => {
  vi.mocked(metadataApi.snapshot).mockResolvedValue(aSnapshot())
})

describe('ArtworkPicker', () => {
  it('ArtworkPicker_reports_a_failed_snapshot_read_instead_of_claiming_there_is_no_artwork', async () => {
    // Arrange — the snapshot cannot be read. "None were found" is a claim about the provider's
    // answer, and a read that failed produced no answer to make a claim about.
    vi.mocked(metadataApi.snapshot).mockRejectedValue(
      new ApiError(500, 'metadata.snapshot_failed', 'The snapshot could not be read.'),
    )

    // Act
    renderWithProviders(<ArtworkPicker snapshotId="snapshot-1" workId="work-1" open onClose={vi.fn()} />)

    // Assert
    expect(await screen.findByText('Artwork could not be loaded')).toBeInTheDocument()
    expect(screen.getByText('The snapshot could not be read.')).toBeInTheDocument()
    expect(screen.queryByText(NO_CANDIDATES_COPY)).not.toBeInTheDocument()
  })

  it('ArtworkPicker_still_says_no_candidates_when_the_provider_genuinely_returned_none', async () => {
    // Arrange — the contrast case: a successful read of a snapshot with no artwork.
    vi.mocked(metadataApi.snapshot).mockResolvedValue(aSnapshot({ artwork: [] }))

    // Act
    renderWithProviders(<ArtworkPicker snapshotId="snapshot-1" workId="work-1" open onClose={vi.fn()} />)

    // Assert
    expect(await screen.findByText(NO_CANDIDATES_COPY)).toBeInTheDocument()
    expect(screen.queryByText('Artwork could not be loaded')).not.toBeInTheDocument()
  })

  it('ArtworkPicker_explains_a_refused_selection_instead_of_leaving_the_poster_unchanged_in_silence', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(metadataApi.selectArtwork).mockRejectedValue(
      new ApiError(409, 'metadata.artwork_conflict', 'That artwork is no longer available.'),
    )
    renderWithProviders(<ArtworkPicker snapshotId="snapshot-1" workId="work-1" open onClose={vi.fn()} />)

    // Act — the candidate is a real toggle button carrying its own pressed state.
    await user.click(await screen.findByRole('button', { pressed: false }))

    // Assert
    expect(await screen.findByText('That artwork is no longer available.')).toBeInTheDocument()
  })
})
