/** One subtitle cue: when it shows, when it goes, and its lines as plain text. */
export interface Cue {
  start: number
  end: number
  text: string
}

const TIMING = /^\s*((?:\d+:)?\d{1,2}:\d{2}\.\d{1,3})\s+-->\s+((?:\d+:)?\d{1,2}:\d{2}\.\d{1,3})/

function seconds(timestamp: string): number {
  const [clock, millis = '0'] = timestamp.split('.')
  const parts = (clock ?? '').split(':').map(Number)
  const [h, m, s] = parts.length === 3 ? parts : [0, ...parts]
  return (h ?? 0) * 3600 + (m ?? 0) * 60 + (s ?? 0) + Number(millis.padEnd(3, '0')) / 1000
}

/**
 * The cues of a WebVTT file, in the order they start. Markup inside a cue (`<i>`, `<c.yellow>`, voice
 * spans) is dropped and entities are decoded, so what is rendered is only ever text — the file came
 * from a subtitle site and is never treated as HTML.
 */
export function parseVtt(source: string): Cue[] {
  const cues: Cue[] = []
  const blocks = source.replace(/\r\n?/g, '\n').split(/\n{2,}/)
  for (const block of blocks) {
    const lines = block.split('\n')
    const timingAt = lines.findIndex((line) => TIMING.test(line))
    if (timingAt < 0) continue
    const match = TIMING.exec(lines[timingAt] ?? '')
    if (!match?.[1] || !match[2]) continue
    const text = lines
      .slice(timingAt + 1)
      .map(plainText)
      .join('\n')
      .trim()
    if (text) cues.push({ start: seconds(match[1]), end: seconds(match[2]), text })
  }
  return cues.sort((a, b) => a.start - b.start)
}

const ENTITIES: Record<string, string> = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&nbsp;': ' ', '&lrm;': '', '&rlm;': '' }

function plainText(line: string): string {
  return line.replace(/<[^>]*>/g, '').replace(/&(?:amp|lt|gt|nbsp|lrm|rlm);/g, (entity) => ENTITIES[entity] ?? entity)
}

/** The text on screen at `time` (seconds into the file): every cue that covers it, top to bottom. */
export function activeText(cues: readonly Cue[], time: number): string {
  const showing: string[] = []
  for (const cue of cues) {
    if (cue.start > time) break
    if (cue.end > time) showing.push(cue.text)
  }
  return showing.join('\n')
}
