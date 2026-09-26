import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { EvaluatedCandidate } from '@/api/types'
import { ReleaseRow } from './ReleaseRow'

function aCandidate(overrides: Partial<EvaluatedCandidate> = {}): EvaluatedCandidate {
  return {
    evaluationId: 'eval-1',
    releaseGuid: 'g1',
    releaseTitle: 'The.Matrix.1999.1080p.BluRay.x264-GRP',
    indexerName: 'Idx',
    protocol: 'Torrent',
    sizeBytes: 5_000_000_000,
    seeders: 12,
    leechers: null,
    publishedAt: null,
    verdict: 'Accepted',
    customFormatScore: 0,
    episodeCoverage: 1,
    isRecommended: false,
    blocked: false,
    blockId: null,
    blockReason: null,
    grabOverridesVerdict: false,
    reasons: [
      {
        rule: 'QualityAllowedByProfile',
        property: 'quality',
        profileValue: 'allowed set',
        actualValue: 'BluRay/P1080',
        outcome: 'Pass',
        rejection: null,
      },
    ],
    ...overrides,
  }
}

const rejected = aCandidate({
  verdict: 'RejectedPermanent',
  grabOverridesVerdict: true,
  reasons: [
    {
      rule: 'QualityAllowedByProfile',
      property: 'quality',
      profileValue: 'allowed set',
      actualValue: 'Cam/Unknown',
      outcome: 'Fail',
      rejection: 'Permanent',
    },
  ],
})

describe('ReleaseRow', () => {
  it('ReleaseRow_grabs_an_accepted_release_directly', async () => {
    // Arrange — nothing is being overridden, so nothing needs confirming.
    const user = userEvent.setup()
    const onGrab = vi.fn()
    render(<ReleaseRow candidate={aCandidate()} onGrab={onGrab} onBlock={vi.fn()} onUnblock={vi.fn()} grabbing={false} grabbed={false} blocking={false} unblocking={false} />)

    // Act
    await user.click(screen.getByRole('button', { name: 'Grab' }))

    // Assert
    expect(onGrab).toHaveBeenCalledTimes(1)
  })

  it('ReleaseRow_confirms_before_overriding_a_rejection', async () => {
    // Arrange — downloading what the profile refused is a deliberate act, not a stray click.
    const user = userEvent.setup()
    const onGrab = vi.fn()
    render(<ReleaseRow candidate={rejected} onGrab={onGrab} onBlock={vi.fn()} onUnblock={vi.fn()} grabbing={false} grabbed={false} blocking={false} unblocking={false} />)

    // Act
    await user.click(screen.getByRole('button', { name: 'Grab' }))

    // Assert — the first click only asks.
    expect(onGrab).not.toHaveBeenCalled()
    expect(screen.getByText(/Download it anyway\?/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Download anyway' }))
    expect(onGrab).toHaveBeenCalledTimes(1)
  })

  it('ReleaseRow_cancelling_the_override_grabs_nothing', async () => {
    const user = userEvent.setup()
    const onGrab = vi.fn()
    render(<ReleaseRow candidate={rejected} onGrab={onGrab} onBlock={vi.fn()} onUnblock={vi.fn()} grabbing={false} grabbed={false} blocking={false} unblocking={false} />)

    await user.click(screen.getByRole('button', { name: 'Grab' }))
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(onGrab).not.toHaveBeenCalled()
    expect(screen.queryByText(/Download it anyway\?/)).not.toBeInTheDocument()
  })

  it('ReleaseRow_reveals_the_reasons_on_demand', async () => {
    // The reasons are the point of the surface, but they are noise until asked for.
    const user = userEvent.setup()
    render(<ReleaseRow candidate={rejected} onGrab={vi.fn()} onBlock={vi.fn()} onUnblock={vi.fn()} grabbing={false} grabbed={false} blocking={false} unblocking={false} />)

    const toggle = screen.getByRole('button', { name: 'Why' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    await user.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    // Stated in text, not by colour alone: whether it failed is the whole content of the line.
    expect(screen.getByText(/Quality allowed by profile/)).toBeInTheDocument()
    expect(screen.getByText(/— failed/)).toBeInTheDocument()
  })

  it('ReleaseRow_blocks_only_after_a_reason_and_refuses_to_grab_while_blocked', async () => {
    const user = userEvent.setup()
    const onBlock = vi.fn()
    const onGrab = vi.fn()
    const { rerender } = render(
      <ReleaseRow
        candidate={aCandidate()}
        onGrab={onGrab}
        onBlock={onBlock}
        onUnblock={vi.fn()}
        grabbing={false}
        grabbed={false}
        blocking={false}
        unblocking={false}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'Block' }))
    await user.click(screen.getByRole('button', { name: 'Block release' }))
    expect(onBlock).not.toHaveBeenCalled()
    expect(await screen.findByText('A block needs a reason, of at most 500 characters.')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/Why block/), 'Known bad encode')
    await user.click(screen.getByRole('button', { name: 'Block release' }))
    expect(onBlock).toHaveBeenCalledWith('Known bad encode')

    rerender(
      <ReleaseRow
        candidate={aCandidate({ blocked: true, blockId: 'block-1', blockReason: 'Known bad encode' })}
        onGrab={onGrab}
        onBlock={onBlock}
        onUnblock={vi.fn()}
        grabbing={false}
        grabbed={false}
        blocking={false}
        unblocking={false}
      />,
    )
    expect(screen.getByRole('button', { name: 'Grab' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Grab' }))
    expect(onGrab).not.toHaveBeenCalled()
  })

  it('ReleaseRow_grabs_a_release_rejected_only_for_a_lifted_block_without_asking', async () => {
    // Arrange — blocked when the search ran, so the verdict says rejected; unblocked since, so taking it
    // overrides nothing the profile said, and the server marks it that way.
    const user = userEvent.setup()
    const onGrab = vi.fn()
    const unblocked = aCandidate({
      verdict: 'RejectedPermanent',
      grabOverridesVerdict: false,
      reasons: [
        {
          rule: 'ReleaseBlocked',
          property: 'releaseGuid',
          profileValue: 'not blocked',
          actualValue: 'Known bad encode',
          outcome: 'Fail',
          rejection: 'Permanent',
        },
      ],
    })
    render(<ReleaseRow candidate={unblocked} onGrab={onGrab} onBlock={vi.fn()} onUnblock={vi.fn()} grabbing={false} grabbed={false} blocking={false} unblocking={false} />)

    // Act
    await user.click(screen.getByRole('button', { name: 'Grab' }))

    // Assert
    expect(onGrab).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: 'Download anyway' })).not.toBeInTheDocument()
  })

  it('ReleaseRow_offers_no_grab_once_it_is_queued', () => {
    render(<ReleaseRow candidate={aCandidate()} onGrab={vi.fn()} onBlock={vi.fn()} onUnblock={vi.fn()} grabbing={false} grabbed blocking={false} unblocking={false} />)

    expect(screen.queryByRole('button', { name: 'Grab' })).not.toBeInTheDocument()
    expect(screen.getByText('Queued')).toBeInTheDocument()
  })
})

describe('ReleaseRow source', () => {
  it('ReleaseRow_says_which_indexer_offered_the_release_and_how_many_peers_it_has', () => {
    render(
      <ReleaseRow
        candidate={aCandidate({ indexerName: 'Example Tracker', seeders: 304, leechers: 27 })}
        grabbed={false}
        grabbing={false}
        onGrab={vi.fn()}
        onBlock={vi.fn()}
        onUnblock={vi.fn()}
        blocking={false}
        unblocking={false}
      />,
    )

    expect(screen.getByText('Example Tracker')).toBeInTheDocument()
    expect(screen.getByText('304 seeders')).toBeInTheDocument()
    expect(screen.getByText('27 leechers')).toBeInTheDocument()
  })

  it('ReleaseRow_says_when_the_indexer_did_not_report_its_swarm', () => {
    render(
      <ReleaseRow
        candidate={aCandidate({ seeders: null, leechers: null })}
        grabbed={false}
        grabbing={false}
        onGrab={vi.fn()}
        onBlock={vi.fn()}
        onUnblock={vi.fn()}
        blocking={false}
        unblocking={false}
      />,
    )

    expect(screen.getByText('Peers not reported')).toBeInTheDocument()
  })
})
