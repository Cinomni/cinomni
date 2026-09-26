import { describe, expect, it } from 'vitest'
import { formatEpisodeCode } from './format'

describe('formatEpisodeCode', () => {
  it('formatEpisodeCode_zero_pads', () => {
    // Arrange / Act / Assert — single-digit numbers are padded to the canonical SxxEyy width.
    expect(formatEpisodeCode(1, 2)).toBe('S01E02')
    expect(formatEpisodeCode(0, 7)).toBe('S00E07')
  })

  it('keeps every digit of a number wider than the padding', () => {
    expect(formatEpisodeCode(12, 345)).toBe('S12E345')
  })

  it('renders a season on its own when there is no episode number', () => {
    expect(formatEpisodeCode(3)).toBe('S03')
    expect(formatEpisodeCode(3, null)).toBe('S03')
  })
})
