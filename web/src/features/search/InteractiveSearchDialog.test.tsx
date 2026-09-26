import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { decisionApi } from '@/api/endpoints'
import type { ReleaseEvaluation } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { InteractiveSearchDialog } from './InteractiveSearchDialog'

vi.mock('@/api/endpoints', () => ({
  decisionApi: { evaluations: vi.fn(), search: vi.fn(), grab: vi.fn(), block: vi.fn(), unblock: vi.fn() },
}))

function anEvaluation(overrides: Partial<ReleaseEvaluation> = {}): ReleaseEvaluation {
  return {
    id: 'eval-1',
    releaseGuid: 'guid-1',
    releaseTitle: 'Arrival.2016.1080p.BluRay',
    verdict: 'Accepted',
    customFormatScore: 0,
    reasons: [],
    ...overrides,
  }
}

function renderDialog() {
  renderWithProviders(<InteractiveSearchDialog targetId="target-1" title="Arrival" open onClose={vi.fn()} />)
}

describe('InteractiveSearchDialog previously evaluated releases', () => {
  it('InteractiveSearchDialog_lets_a_release_from_the_last_search_be_grabbed_without_searching_again', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(decisionApi.evaluations).mockResolvedValue([anEvaluation()])
    vi.mocked(decisionApi.grab).mockResolvedValue({
      evaluationId: 'eval-1',
      targetId: 'target-1',
      releaseTitle: 'Arrival.2016.1080p.BluRay',
      verdict: 'Accepted',
      overrodeVerdict: false,
    })
    renderDialog()

    // Act
    await user.click(await screen.findByRole('button', { name: /Grab Arrival\.2016\.1080p\.BluRay/ }))

    // Assert — the grab goes to the evaluation the platform already made, and the row says it is queued.
    expect(decisionApi.grab).toHaveBeenCalledWith('eval-1')
    expect(await screen.findByText('Queued')).toBeInTheDocument()
    expect(decisionApi.search).not.toHaveBeenCalled()
  })

  it('InteractiveSearchDialog_asks_twice_before_taking_a_release_the_profile_did_not_accept', async () => {
    const user = userEvent.setup()
    vi.mocked(decisionApi.evaluations).mockResolvedValue([anEvaluation({ verdict: 'RejectedPermanent' })])
    vi.mocked(decisionApi.grab).mockResolvedValue({
      evaluationId: 'eval-1',
      targetId: 'target-1',
      releaseTitle: 'Arrival.2016.1080p.BluRay',
      verdict: 'RejectedPermanent',
      overrodeVerdict: true,
    })
    renderDialog()

    await user.click(await screen.findByRole('button', { name: /Grab Arrival/ }))
    expect(decisionApi.grab).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Download anyway' }))

    expect(decisionApi.grab).toHaveBeenCalledWith('eval-1')
  })

  it('InteractiveSearchDialog_says_when_the_search_behind_an_old_release_is_gone', async () => {
    const { ApiError } = await import('@/lib/api')
    const user = userEvent.setup()
    vi.mocked(decisionApi.evaluations).mockResolvedValue([anEvaluation()])
    vi.mocked(decisionApi.grab).mockRejectedValue(
      new ApiError(409, 'decision.release_unavailable', 'This release is no longer available from the search that found it. Search again.'),
    )
    renderDialog()

    await user.click(await screen.findByRole('button', { name: /Grab Arrival/ }))

    expect(await screen.findByText(/no longer available.*Search again/)).toBeInTheDocument()
  })
})

describe('InteractiveSearchDialog history source', () => {
  it('InteractiveSearchDialog_shows_where_a_previously_evaluated_release_came_from', async () => {
    vi.mocked(decisionApi.evaluations).mockResolvedValue([
      anEvaluation({ indexerName: 'Indexer Two', seeders: 8, leechers: 3 }),
    ])

    renderDialog()

    expect(await screen.findByText('Indexer Two')).toBeInTheDocument()
    expect(screen.getByText('8 seeders')).toBeInTheDocument()
    expect(screen.getByText('3 leechers')).toBeInTheDocument()
  })
})
