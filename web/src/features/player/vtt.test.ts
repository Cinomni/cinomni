import { describe, expect, it } from 'vitest'
import { activeText, parseVtt } from './vtt'

describe('parseVtt', () => {
  it('parseVtt_reads_the_cues_in_start_order_with_hours_optional', () => {
    // Arrange
    const source = 'WEBVTT\n\n2\n00:00:05.000 --> 00:00:06.000\nSecond\n\n1\n00:01.500 --> 00:03.000\nFirst\n'

    // Act
    const cues = parseVtt(source)

    // Assert
    expect(cues).toEqual([
      { start: 1.5, end: 3, text: 'First' },
      { start: 5, end: 6, text: 'Second' },
    ])
  })

  it('parseVtt_keeps_only_the_text_of_a_cue_never_its_markup', () => {
    // Arrange — the file came from a subtitle site: markup is dropped, entities decoded, nothing rendered as HTML.
    const source = 'WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.000 line:10%\r\n<i>Hi</i> <c.yellow>&lt;there&gt;</c>\r\n<img src=x onerror=alert(1)>Bye &amp; out'

    // Act
    const [cue] = parseVtt(source)

    // Assert
    expect(cue?.text).toBe('Hi <there>\nBye & out')
  })

  it('parseVtt_skips_blocks_that_are_not_cues', () => {
    expect(parseVtt('WEBVTT\n\nNOTE a comment\n\nSTYLE\n::cue { color: red }')).toEqual([])
  })
})

describe('activeText', () => {
  it('activeText_shows_every_cue_covering_the_time_and_nothing_between_cues', () => {
    // Arrange
    const cues = parseVtt('WEBVTT\n\n00:01.000 --> 00:04.000\nOne\n\n00:02.000 --> 00:03.000\nTwo\n')

    // Act + Assert
    expect(activeText(cues, 2.5)).toBe('One\nTwo')
    expect(activeText(cues, 3.5)).toBe('One')
    expect(activeText(cues, 4)).toBe('')
  })
})
