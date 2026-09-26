import type { ClientCapability } from '@/api/types'

/**
 * Probe what this browser can actually play, so the server plans Direct Play when possible and only
 * transcodes what the client can't decode. Codec names match ffprobe's (what the Library stores).
 */
export function detectCapability(): ClientCapability {
  const video = document.createElement('video')
  const can = (type: string) => video.canPlayType(type) !== ''

  const videoCodecs: string[] = []
  if (can('video/mp4; codecs="avc1.42E01E"')) videoCodecs.push('h264')
  if (can('video/mp4; codecs="hvc1.1.6.L93.B0"') || can('video/mp4; codecs="hev1"')) videoCodecs.push('hevc')
  if (can('video/webm; codecs="vp8"')) videoCodecs.push('vp8')
  if (can('video/webm; codecs="vp9"') || can('video/mp4; codecs="vp09.00.10.08"')) videoCodecs.push('vp9')
  if (can('video/mp4; codecs="av01.0.05M.08"')) videoCodecs.push('av1')

  const audioCodecs: string[] = []
  if (can('audio/mp4; codecs="mp4a.40.2"')) audioCodecs.push('aac')
  if (can('audio/mpeg')) audioCodecs.push('mp3')
  if (can('audio/webm; codecs="opus"')) audioCodecs.push('opus')
  if (can('audio/webm; codecs="vorbis"') || can('audio/ogg; codecs="vorbis"')) audioCodecs.push('vorbis')
  if (can('audio/mp4; codecs="flac"') || can('audio/ogg; codecs="flac"')) audioCodecs.push('flac')
  if (can('audio/mp4; codecs="ac-3"')) audioCodecs.push('ac3')
  if (can('audio/mp4; codecs="ec-3"')) audioCodecs.push('eac3')

  const containers = ['mp4']
  if (videoCodecs.includes('vp8') || videoCodecs.includes('vp9')) containers.push('webm')

  const screenHeight = window.screen?.height
  const maxHeight = screenHeight ? Math.max(screenHeight, 1080) : null

  return { containers, videoCodecs, audioCodecs, maxHeight }
}
