import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { systemApi } from '@/api/endpoints'
import type { SystemInfo } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { BuildFooter } from './BuildFooter'

vi.mock('@/api/endpoints', () => ({ systemApi: { info: vi.fn() } }))

function aBuild(overrides: Partial<SystemInfo> = {}): SystemInfo {
  return {
    version: '1.2.3',
    informationalVersion: '1.2.3-beta.1+a1b2c3d',
    commit: 'a1b2c3d',
    buildDate: '2026-09-02T01:00:00+00:00',
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(systemApi.info).mockResolvedValue(aBuild())
})

describe('BuildFooter', () => {
  it('BuildFooter_names_the_build_with_the_string_that_identifies_it_unambiguously', async () => {
    // Arrange — the point of the footer is that a member reporting a problem can say what they are
    // on, so it shows the suffixed string rather than the plain release number.
    renderWithProviders(<BuildFooter />)

    // Assert
    expect(await screen.findByText(/1\.2\.3-beta\.1\+a1b2c3d/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Source code (AGPLv3+)' })).toHaveAttribute(
      'href',
      'https://github.com/Cinomni/cinomni/tree/a1b2c3d',
    )
  })

  it('BuildFooter_keeps_the_source_link_when_the_build_cannot_be_read', async () => {
    // Arrange — this is chrome on every page, not a screen. A strip of page furniture announcing a
    // failed read would shout, on every navigation, about the one thing nobody came here for. The
    // console's system panel is where that failure is a finding worth stating.
    vi.mocked(systemApi.info).mockRejectedValue(new ApiError(503, 'system.unavailable', 'Unavailable.'))

    // Act
    renderWithProviders(<BuildFooter />)

    // Assert — the build identity stays absent, while the source remains discoverable.
    await waitFor(() => expect(systemApi.info).toHaveBeenCalled())
    expect(screen.getByRole('link', { name: 'Source code (AGPLv3+)' })).toHaveAttribute(
      'href',
      'https://github.com/Cinomni/cinomni',
    )
    expect(screen.queryByText(/Unavailable/)).not.toBeInTheDocument()
  })
})
