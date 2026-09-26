import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from '@/test/render'
import { aWork } from '@/test/factories'
import { MediaPoster } from './MediaPoster'

describe('MediaPoster', () => {
  it('MediaPoster_shows_partial_availability_for_a_series', () => {
    // Arrange — a show with three of its ten episodes on disk: neither empty nor complete.
    const work = aWork({ kind: 'Series', title: 'The Wire', episodeCount: 10, availableEpisodeCount: 3 })

    // Act
    renderWithProviders(<MediaPoster work={work} />)

    // Assert — the middle state, not the boolean the movie slice shipped.
    expect(screen.getByText('Partly in library — 3 of 10 episodes')).toBeInTheDocument()
    expect(screen.queryByText(/^In library/)).not.toBeInTheDocument()
    expect(screen.getByRole('link')).toHaveAttribute('href', '/series/work-1')
  })

  it('shows a complete series once every episode is in the library', () => {
    renderWithProviders(<MediaPoster work={aWork({ kind: 'Series', episodeCount: 4, availableEpisodeCount: 4 })} />)

    expect(screen.getByText('In library — 4 of 4 episodes')).toBeInTheDocument()
  })

  it('falls back to the boolean for a movie and links to the movie page', () => {
    renderWithProviders(<MediaPoster work={aWork({ kind: 'Movie', hasAsset: true })} />)

    expect(screen.getByText('In library')).toBeInTheDocument()
    expect(screen.getByRole('link')).toHaveAttribute('href', '/works/work-1')
  })
})

describe('MediaPoster progress', () => {
  it('MediaPoster_draws_watch_progress_only_when_there_is_some', () => {
    const { rerender } = renderWithProviders(<MediaPoster work={aWork({ title: 'Arrival' })} progress={0.4} />)

    expect(screen.getByRole('progressbar', { name: 'Arrival progress' })).toHaveAttribute('aria-valuenow', '40')

    rerender(<MediaPoster work={aWork({ title: 'Arrival' })} progress={0} />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })
})
