import { afterEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from '@/test/render'
import { SourceOffer } from './SourceOffer'

afterEach(() => vi.unstubAllEnvs())

describe('SourceOffer', () => {
  it('uses_the_build_source_url_for_a_modified_distribution', () => {
    vi.stubEnv('VITE_SOURCE_URL', 'https://example.org/cinomni/tree/release-1')
    renderWithProviders(<SourceOffer commit="a1b2c3d" />)

    expect(screen.getByRole('link', { name: 'Source code (AGPLv3+)' })).toHaveAttribute(
      'href',
      'https://example.org/cinomni/tree/release-1',
    )
  })

  it('rejects_a_non_http_source_url', () => {
    vi.stubEnv('VITE_SOURCE_URL', 'javascript:alert(1)')
    renderWithProviders(<SourceOffer commit="a1b2c3d" />)

    expect(screen.getByRole('link', { name: 'Source code (AGPLv3+)' })).toHaveAttribute(
      'href',
      'https://github.com/Cinomni/cinomni/tree/a1b2c3d',
    )
  })
})
