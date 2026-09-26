import type { MediaStream, VideoRangeType } from '@/api/types'

const RANGE_LABEL: Record<VideoRangeType, string | null> = {
  Sdr: null,
  Hdr10: 'HDR10',
  Hdr10Plus: 'HDR10+',
  DoVi: 'Dolby Vision',
  Hlg: 'HLG',
}

const CODEC_LABEL: Record<string, string> = {
  hevc: 'HEVC',
  h265: 'HEVC',
  h264: 'H.264',
  avc: 'H.264',
  av1: 'AV1',
  vp9: 'VP9',
  mpeg2video: 'MPEG-2',
  aac: 'AAC',
  ac3: 'Dolby Digital',
  eac3: 'Dolby Digital+',
  truehd: 'TrueHD',
  dts: 'DTS',
  flac: 'FLAC',
  opus: 'Opus',
  mp3: 'MP3',
}

function codecLabel(codec: string | null): string | null {
  if (!codec) return null
  return CODEC_LABEL[codec.toLowerCase()] ?? codec.toUpperCase()
}

/** The common name of a vertical resolution; odd heights (a 1040-line scope crop) round to their class. */
function resolutionLabel(height: number | null): string | null {
  if (!height || height <= 0) return null
  if (height >= 2000) return '4K'
  if (height >= 1000) return '1080p'
  if (height >= 700) return '720p'
  return `${height}p`
}

function channelLabel(channels: number | null): string | null {
  if (!channels) return null
  if (channels === 1) return 'Mono'
  if (channels === 2) return 'Stereo'
  if (channels === 6) return '5.1'
  if (channels === 8) return '7.1'
  return `${channels} ch`
}

function languageName(code: string | null): string | null {
  if (!code) return null
  try {
    return new Intl.DisplayNames(undefined, { type: 'language' }).of(code) ?? code
  } catch {
    // Not a BCP 47 tag the runtime knows (three-letter ISO 639-2 codes often are not): show it as-is.
    return code
  }
}

export interface QualitySummary {
  /** "4K · HEVC · Dolby Vision" */
  video: string | null
  /** "English · Dolby Digital+ · 5.1" */
  audio: string | null
  /** Distinct subtitle languages, in stream order. */
  subtitles: string[]
}

/**
 * A viewer's reading of a file's streams — resolution, dynamic range, the main audio track and which
 * subtitle languages exist — in words rather than codec ids. The raw streams stay available in the
 * advanced layer; this is only the sentence a person would say about the file.
 */
export function summarizeStreams(streams: readonly MediaStream[]): QualitySummary {
  const video = streams.find((stream) => stream.type === 'Video')
  const audio = streams.find((stream) => stream.type === 'Audio' && stream.isDefault) ?? streams.find((stream) => stream.type === 'Audio')
  const subtitleLanguages = streams
    .filter((stream) => stream.type === 'Subtitle')
    .map((stream) => languageName(stream.language))
    .filter((language): language is string => language != null)

  const join = (parts: ReadonlyArray<string | null>) => {
    const present = parts.filter((part): part is string => part != null)
    return present.length > 0 ? present.join(' · ') : null
  }

  return {
    video: video
      ? join([resolutionLabel(video.height), codecLabel(video.codec), video.videoRangeType ? RANGE_LABEL[video.videoRangeType] : null])
      : null,
    audio: audio ? join([languageName(audio.language), codecLabel(audio.codec), channelLabel(audio.channels)]) : null,
    subtitles: [...new Set(subtitleLanguages)],
  }
}
