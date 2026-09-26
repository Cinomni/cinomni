import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { downloadsApi, healthApi, systemApi } from '@/api/endpoints'
import type { SystemInfo, TunnelEgressStatus } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { SystemPage } from './SystemPage'

vi.mock('@/api/endpoints', () => ({
  healthApi: { ready: vi.fn() },
  downloadsApi: { tunnel: vi.fn() },
  systemApi: { info: vi.fn() },
}))

function aBuild(overrides: Partial<SystemInfo> = {}): SystemInfo {
  return {
    version: '1.0.0',
    informationalVersion: '1.0.0',
    commit: null,
    buildDate: null,
    ...overrides,
  }
}

function aTunnel(overrides: Partial<TunnelEgressStatus> = {}): TunnelEgressStatus {
  return {
    configured: true,
    policy: 'Block',
    verified: true,
    reason: 'tunnel-egress-verified',
    tunnelDevice: 'tun0',
    heldTaskCount: 0,
    transitionSequence: 4,
    observedAt: '2026-07-30T10:00:00Z',
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(healthApi.ready).mockResolvedValue({ status: 'Healthy', checks: [] })
  vi.mocked(downloadsApi.tunnel).mockResolvedValue(aTunnel())
  vi.mocked(systemApi.info).mockResolvedValue(aBuild())
})

describe('SystemPage build panel', () => {
  it('SystemPage_states_a_build_identity_it_could_not_read_instead_of_hiding_it', async () => {
    // Arrange — the layout footer swallows this failure because it is chrome. This page is
    // diagnostic, so here an unreadable build identity is the finding, not a reason to show nothing.
    vi.mocked(systemApi.info).mockRejectedValue(
      new ApiError(503, 'system.unavailable', 'The build identity could not be read.'),
    )

    // Act
    renderWithProviders(<SystemPage />)

    // Assert
    expect(await screen.findByText('Build identity could not be read')).toBeInTheDocument()
    expect(screen.getByText('The build identity could not be read.')).toBeInTheDocument()
  })

  it('SystemPage_does_not_repeat_the_same_version_twice_on_an_unstamped_build', async () => {
    // Arrange — a development build reports the same string in both fields; showing it as two rows
    // would read as a rendering mistake rather than as the two distinct facts it is on a real build.
    renderWithProviders(<SystemPage />)

    // Assert
    expect(await screen.findAllByText('1.0.0')).toHaveLength(1)
    expect(screen.queryByText('Release number')).not.toBeInTheDocument()
  })

  it('SystemPage_leaves_out_commit_and_build_date_the_build_never_carried', async () => {
    // Arrange — null here is a fact about the build, not missing data. An empty "Commit —" row would
    // dress it up as something that failed to load.
    renderWithProviders(<SystemPage />)

    // Assert
    await screen.findByText('Version')
    expect(screen.queryByText('Commit')).not.toBeInTheDocument()
    expect(screen.queryByText('Built')).not.toBeInTheDocument()
    expect(screen.getByText(/Nothing stamped them/)).toBeInTheDocument()
  })

  it('SystemPage_shows_the_release_number_commit_and_date_a_stamped_build_does_carry', async () => {
    // Arrange — the contrast case: an image build, where all four fields say something different.
    vi.mocked(systemApi.info).mockResolvedValue(
      aBuild({
        version: '1.2.3',
        informationalVersion: '1.2.3-beta.1+a1b2c3d',
        commit: 'a1b2c3d',
        buildDate: '2026-09-02T01:00:00+00:00',
      }),
    )

    // Act
    renderWithProviders(<SystemPage />)

    // Assert
    expect(await screen.findByText('1.2.3-beta.1+a1b2c3d')).toBeInTheDocument()
    expect(screen.getByText('1.2.3')).toBeInTheDocument()
    expect(screen.getByText('a1b2c3d')).toBeInTheDocument()
    expect(screen.queryByText(/Nothing stamped them/)).not.toBeInTheDocument()
  })
})

describe('SystemPage', () => {
  it('SystemPage_names_the_dependency_behind_a_degraded_readiness', async () => {
    // Arrange — readiness answers 200 when degraded, so the status alone would not tell an operator
    // which dependency slipped.
    vi.mocked(healthApi.ready).mockResolvedValue({
      status: 'Degraded',
      checks: [
        { name: 'postgres', status: 'Healthy' },
        { name: 'sidecar', status: 'Degraded' },
      ],
    })

    // Act
    renderWithProviders(<SystemPage />)

    // Assert
    expect(await screen.findByText('Torrent sidecar')).toBeInTheDocument()
    expect(screen.getAllByText('Degraded').length).toBeGreaterThan(0)
  })

  it('SystemPage_reads_an_unconfigured_tunnel_as_ordinary_rather_than_as_a_fault', async () => {
    // Arrange — the VPN is opt-in, so no tunnel is a supported installation, not a misconfiguration.
    vi.mocked(downloadsApi.tunnel).mockResolvedValue(aTunnel({ configured: false, verified: false, reason: 'tunnel-guard-disabled' }))

    // Act
    renderWithProviders(<SystemPage />)

    // Assert
    expect(await screen.findByText('No tunnel')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('SystemPage_warns_that_a_configured_tunnel_is_unverified_and_says_why', async () => {
    // Arrange — silence is never health: an unreachable sidecar is unverified, not fine.
    vi.mocked(downloadsApi.tunnel).mockResolvedValue(
      aTunnel({ verified: false, reason: 'sidecar-unreachable', heldTaskCount: 2 }),
    )

    // Act
    renderWithProviders(<SystemPage />)

    // Assert — the machine-readable reason reaches the operator as a sentence, never raw.
    expect(await screen.findByText('Unverified')).toBeInTheDocument()
    expect(screen.getByText(/sidecar cannot be reached/i)).toBeInTheDocument()
    expect(screen.queryByText('sidecar-unreachable')).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })
})
