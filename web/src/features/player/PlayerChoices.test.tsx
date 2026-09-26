import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, libraryApi, playbackApi } from '@/api/endpoints'
import type { PlaybackMedia, PlaybackTicket } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { aWork } from '@/test/factories'
import { PlayerPage } from './PlayerPage'

vi.mock('hls.js', () => {
  class FakeHls {
    static Events = { ERROR: 'hlsError' }
    static ErrorTypes = { NETWORK_ERROR: 'networkError', MEDIA_ERROR: 'mediaError' }
    static isSupported = () => true
    on() {}
    loadSource() {}
    attachMedia() {}
    destroy() {}
  }
  return { default: FakeHls }
})

vi.mock('@/api/endpoints', () => ({
  catalogApi: { get: vi.fn(), episode: vi.fn() },
  libraryApi: { asset: vi.fn() },
  playbackApi: {
    start: vi.fn(),
    progress: vi.fn(),
    stop: vi.fn(),
    stopOnUnload: vi.fn(),
    subtitles: vi.fn(),
    chooseSubtitle: vi.fn(),
  },
}))

const TICKS = 10_000_000

function aMedia(overrides: Partial<PlaybackMedia> = {}): PlaybackMedia {
  return {
    audioTracks: [
      { index: 1, language: 'eng', codec: 'aac', channels: 6, isDefault: true },
      { index: 2, language: 'spa', codec: 'ac3', channels: 6, isDefault: false },
    ],
    subtitleTracks: [
      {
        index: 3,
        language: 'eng',
        codec: 'subrip',
        isForced: false,
        isDefault: false,
        isExternal: false,
        url: '/api/playback/sessions/session-1/subtitles/3',
      },
      {
        index: 4,
        language: 'eng',
        codec: 'hdmv_pgs_subtitle',
        isForced: false,
        isDefault: false,
        isExternal: false,
        url: null,
      },
    ],
    qualities: [
      { id: 'original', maxWidth: null, maxHeight: null, maxBitrateKbps: null },
      { id: '720p', maxWidth: 1280, maxHeight: 720, maxBitrateKbps: 4000 },
    ],
    quality: 'original',
    streamOffsetTicks: 0,
    durationTicks: 7200 * TICKS,
    ...overrides,
  }
}

function aTicket(overrides: Partial<PlaybackTicket> = {}): PlaybackTicket {
  return {
    sessionId: 'session-1',
    method: 'DirectPlay',
    streamUrl: '/api/playback/sessions/session-1/stream',
    resumePositionTicks: 0,
    selection: { audio: 1, subtitle: null },
    plan: {
      method: 'DirectPlay',
      transcodeReasons: [],
      decisions: [],
      backend: 'Software',
      accelerationReasons: [],
      decodeAccelerated: false,
    },
    media: aMedia(),
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(libraryApi.asset).mockResolvedValue({
    asset: {
      id: 'asset-1',
      workId: 'work-1',
      state: 'Active',
      primaryVersionId: 'version-1',
      createdAt: '2026-07-01T00:00:00Z',
      unitIds: ['work-1'],
    },
    targetIds: [],
    unitIds: ['work-1'],
    versions: [],
  })
  vi.mocked(catalogApi.get).mockResolvedValue(aWork({ id: 'work-1', kind: 'Movie', title: 'A Title' }))
  vi.mocked(playbackApi.progress).mockResolvedValue(undefined)
  vi.mocked(playbackApi.stop).mockResolvedValue(undefined)
  vi.mocked(playbackApi.chooseSubtitle).mockResolvedValue(undefined)
})

async function openSettings(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Settings' }))
  return screen.getByRole('dialog', { name: 'Playback settings' })
}

describe('PlayerPage choices', () => {
  it('PlayerPage_opens_a_new_session_with_the_audio_track_the_viewer_picks_keeping_their_other_choices', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    renderWithProviders(<PlayerPage />)

    // Act
    const settings = await openSettings(user)
    await user.click(within(settings).getByRole('button', { name: /Audio/ }))
    await user.click(screen.getByRole('radio', { name: 'Spanish · 5.1 · Dolby Digital' }))

    // Assert — the audio is a new session, from where the viewer is, with the quality and subtitles they had.
    await waitFor(() => expect(playbackApi.start).toHaveBeenCalledTimes(2))
    expect(vi.mocked(playbackApi.start).mock.lastCall?.[0].preferences).toEqual({
      audioStreamIndex: 2,
      quality: 'original',
      subtitlesOff: true,
      startPositionTicks: 0,
    })
  })

  it('PlayerPage_asks_the_server_for_the_quality_the_viewer_picks_from_the_offered_ladder', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    renderWithProviders(<PlayerPage />)

    // Act
    const settings = await openSettings(user)
    await user.click(within(settings).getByRole('button', { name: /Quality/ }))
    await user.click(screen.getByRole('radio', { name: '720p · 4 Mbps' }))

    // Assert
    await waitFor(() => expect(playbackApi.start).toHaveBeenCalledTimes(2))
    expect(vi.mocked(playbackApi.start).mock.lastCall?.[0].preferences).toMatchObject({ quality: '720p' })
  })

  it('PlayerPage_turns_subtitles_on_in_place_shows_the_cue_and_remembers_the_choice', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    vi.mocked(playbackApi.subtitles).mockResolvedValue('WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nHello there\n')
    renderWithProviders(<PlayerPage />)

    // Act
    await user.click(await screen.findByRole('button', { name: 'Turn subtitles on' }))

    // Assert — no new session: the track is fetched with the header and drawn over the picture.
    expect(await screen.findByText('Hello there')).toBeInTheDocument()
    expect(playbackApi.subtitles).toHaveBeenCalledWith('/api/playback/sessions/session-1/subtitles/3', expect.anything())
    expect(playbackApi.chooseSubtitle).toHaveBeenCalledWith('session-1', 3)
    expect(playbackApi.start).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Turn subtitles off' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('PlayerPage_lists_a_picture_subtitle_track_as_unavailable_rather_than_hiding_it', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    renderWithProviders(<PlayerPage />)

    // Act
    const settings = await openSettings(user)
    await user.click(within(settings).getByRole('button', { name: /Subtitles/ }))

    // Assert
    const picture = screen.getByRole('radio', { name: /English \(2\)/ })
    expect(picture).toBeDisabled()
    expect(within(picture).getByText('Image-based, not supported in the browser')).toBeInTheDocument()
  })

  it('PlayerPage_opens_a_new_session_that_burns_in_a_picture_subtitle_the_server_can_draw', async () => {
    // Arrange — the PGS track is one the server can draw into the video.
    const user = userEvent.setup()
    const base = aMedia()
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        media: aMedia({
          subtitleTracks: base.subtitleTracks.map((t) => (t.index === 4 ? { ...t, canBurnIn: true } : t)),
        }),
      }),
    )
    renderWithProviders(<PlayerPage />)

    // Act
    const settings = await openSettings(user)
    await user.click(within(settings).getByRole('button', { name: /Subtitles/ }))
    const picture = screen.getByRole('radio', { name: /English \(2\)/ })
    expect(picture).toBeEnabled()
    expect(within(picture).getByText('Drawn into the video — restarts the stream')).toBeInTheDocument()
    await user.click(picture)

    // Assert — a new session asked for that track, not an in-place switch.
    await waitFor(() => expect(playbackApi.start).toHaveBeenCalledTimes(2))
    const preferences = vi.mocked(playbackApi.start).mock.lastCall?.[0].preferences
    expect(preferences).toMatchObject({ subtitleStreamIndex: 4 })
    expect(preferences?.subtitlesOff).toBeUndefined()
    expect(playbackApi.subtitles).not.toHaveBeenCalled()
  })

  it('PlayerPage_opens_a_new_session_without_subtitles_when_the_viewer_turns_off_a_burned_in_track', async () => {
    // Arrange — this stream carries the PGS track in its picture.
    const user = userEvent.setup()
    const base = aMedia()
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        method: 'Transcode',
        streamUrl: '/api/playback/sessions/session-1/hls/manifest.m3u8',
        selection: { audio: 1, subtitle: 4 },
        media: aMedia({
          subtitleTracks: base.subtitleTracks.map((t) => (t.index === 4 ? { ...t, canBurnIn: true } : t)),
          burnedInSubtitle: 4,
        }),
      }),
    )
    renderWithProviders(<PlayerPage />)

    // Act — the subtitles toggle shows it on; turning it off must take it out of the picture.
    await user.click(await screen.findByRole('button', { name: 'Turn subtitles off' }))

    // Assert
    await waitFor(() => expect(playbackApi.start).toHaveBeenCalledTimes(2))
    const preferences = vi.mocked(playbackApi.start).mock.lastCall?.[0].preferences
    expect(preferences).toMatchObject({ subtitlesOff: true })
    expect(preferences?.subtitleStreamIndex).toBeUndefined()
    expect(playbackApi.chooseSubtitle).not.toHaveBeenCalled()
  })

  it('PlayerPage_restarts_a_conversion_at_a_point_it_has_not_reached_instead_of_waiting_for_it', async () => {
    // Arrange — a transcode that has produced nothing seekable yet.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({ method: 'Transcode', streamUrl: '/api/playback/sessions/session-1/hls/manifest.m3u8' }),
    )
    renderWithProviders(<PlayerPage />)

    // Act — the viewer jumps an hour in with the keyboard on the timeline.
    fireEvent.change(await screen.findByRole('slider', { name: 'Seek' }), { target: { value: '3600' } })

    // Assert
    await waitFor(() => expect(playbackApi.start).toHaveBeenCalledTimes(2))
    expect(vi.mocked(playbackApi.start).mock.lastCall?.[0].preferences).toMatchObject({
      startPositionTicks: 3600 * TICKS,
    })
  })

  it('PlayerPage_shows_the_files_time_for_a_stream_that_starts_part_way_through', async () => {
    // Arrange — the conversion started ten minutes in; its own zero is the file's 10:00.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        method: 'Transcode',
        streamUrl: '/api/playback/sessions/session-1/hls/manifest.m3u8',
        resumePositionTicks: 600 * TICKS,
        media: aMedia({ streamOffsetTicks: 600 * TICKS }),
      }),
    )

    // Act
    renderWithProviders(<PlayerPage />)

    // Assert
    expect(await screen.findByText('10:00')).toBeInTheDocument()
    expect(screen.getByRole('slider', { name: 'Seek' })).toHaveAttribute('aria-valuetext', '10:00 of 2:00:00')
  })
})
