import { useEffect, useState, type RefObject } from 'react'

/** What the controls draw, read off the `<video>` element as it changes. Times are the stream's own. */
export interface VideoState {
  paused: boolean
  currentTime: number
  /** The stream's length so far; infinite or NaN until known, growing while a conversion runs. */
  duration: number
  /** How far ahead the player holds data, measured from the current position's buffered range. */
  bufferedEnd: number
  volume: number
  muted: boolean
  playbackRate: number
  /** Waiting for data: a seek, a stall, or a conversion that has not got that far yet. */
  waiting: boolean
}

const INITIAL: VideoState = {
  paused: true,
  currentTime: 0,
  duration: Number.NaN,
  bufferedEnd: 0,
  volume: 1,
  muted: false,
  playbackRate: 1,
  waiting: true,
}

const EVENTS = [
  'play',
  'pause',
  'timeupdate',
  'durationchange',
  'progress',
  'volumechange',
  'ratechange',
  'waiting',
  'playing',
  'seeking',
  'seeked',
  'loadedmetadata',
  'canplay',
] as const

function bufferedEnd(video: HTMLVideoElement): number {
  const { buffered, currentTime } = video
  for (let i = 0; i < buffered.length; i++) {
    if (buffered.start(i) <= currentTime + 0.5 && buffered.end(i) >= currentTime) return buffered.end(i)
  }
  return currentTime
}

function read(video: HTMLVideoElement, waiting: boolean): VideoState {
  return {
    paused: video.paused,
    currentTime: video.currentTime,
    duration: video.duration,
    bufferedEnd: bufferedEnd(video),
    volume: video.volume,
    muted: video.muted,
    playbackRate: video.playbackRate,
    waiting,
  }
}

/** Subscribes to the media events of the element in `videoRef` and returns its latest state. */
export function useVideoState(videoRef: RefObject<HTMLVideoElement | null>): VideoState {
  const [state, setState] = useState<VideoState>(INITIAL)

  useEffect(() => {
    const video = videoRef.current
    if (!video) return
    let waiting = true
    const update = (event: Event) => {
      if (event.type === 'waiting' || event.type === 'seeking') waiting = true
      if (event.type === 'playing' || event.type === 'seeked' || event.type === 'canplay') waiting = false
      if (event.type === 'pause') waiting = false
      setState(read(video, waiting))
    }
    for (const name of EVENTS) video.addEventListener(name, update)
    return () => {
      for (const name of EVENTS) video.removeEventListener(name, update)
    }
  }, [videoRef])

  return state
}

/** The furthest point the player can seek to right now: the whole file played as-is, what is converted so far otherwise. */
export function seekableEnd(video: HTMLVideoElement): number {
  const { seekable } = video
  if (seekable.length > 0) return seekable.end(seekable.length - 1)
  return Number.isFinite(video.duration) ? video.duration : 0
}
