import type { AudioTrack, QualityOption, SubtitleTrack } from '@/api/types'

let displayNames: Intl.DisplayNames | null | undefined

/** A language code as a name ("spa" → "Spanish"); the code itself when the browser does not know it. */
export function languageName(code: string | null): string {
  if (!code || code === 'und') return 'Unknown language'
  if (displayNames === undefined) {
    try {
      displayNames = new Intl.DisplayNames(['en'], { type: 'language', fallback: 'none' })
    } catch {
      displayNames = null
    }
  }
  try {
    return displayNames?.of(code) ?? code.toUpperCase()
  } catch {
    return code.toUpperCase()
  }
}

const CHANNELS: Record<number, string> = { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }

const CODECS: Record<string, string> = {
  aac: 'AAC',
  ac3: 'Dolby Digital',
  eac3: 'Dolby Digital Plus',
  truehd: 'Dolby TrueHD',
  dts: 'DTS',
  flac: 'FLAC',
  opus: 'Opus',
  mp3: 'MP3',
  vorbis: 'Vorbis',
}

export function audioLabel(track: AudioTrack): string {
  const parts = [languageName(track.language)]
  if (track.channels) parts.push(CHANNELS[track.channels] ?? `${track.channels} ch`)
  if (track.codec) parts.push(CODECS[track.codec.toLowerCase()] ?? track.codec.toUpperCase())
  return parts.join(' · ')
}

export function subtitleLabel(track: SubtitleTrack): string {
  const parts = [languageName(track.language)]
  if (track.isForced) parts.push('Forced')
  if (track.isExternal) parts.push('Downloaded')
  return parts.join(' · ')
}

/** Why a subtitle track cannot be picked, or null when it can. */
export function subtitleUnavailableReason(track: SubtitleTrack): string | null {
  if (track.url) return null
  return track.canBurnIn ? null : 'Image-based, not supported in the browser'
}

/** What picking a track does beyond showing it, or null when it only switches in place. */
export function subtitleNote(track: SubtitleTrack): string | null {
  return !track.url && track.canBurnIn ? 'Drawn into the video — restarts the stream' : null
}

export function qualityLabel(option: QualityOption): string {
  if (option.maxHeight === null || option.maxBitrateKbps === null) return 'Original'
  const mbps = option.maxBitrateKbps / 1000
  return `${option.maxHeight}p · ${Number.isInteger(mbps) ? mbps : mbps.toFixed(1)} Mbps`
}

export const PLAYBACK_RATES = [0.5, 0.75, 1, 1.25, 1.5, 2] as const

export const rateLabel = (rate: number): string => (rate === 1 ? 'Normal' : `${rate}×`)
