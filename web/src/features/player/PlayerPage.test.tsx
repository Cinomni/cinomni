import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, libraryApi, playbackApi } from '@/api/endpoints'
import type { MediaAssetDetail, PlaybackTicket } from '@/api/types'
import { ApiError } from '@/lib/api'
import { openRealtimeStream } from '@/lib/realtime'
import { RealtimeProvider } from '@/realtime/RealtimeProvider'
import { renderWithProviders } from '@/test/render'
import { aWork } from '@/test/factories'
import { PlayerPage } from './PlayerPage'

vi.mock('@/lib/realtime', () => ({ openRealtimeStream: vi.fn(() => () => undefined) }))

/**
 * hls.js as far as the player uses it. jsdom has no MediaSource, so the real one reports itself
 * unsupported and never reaches the code under test; this one records the error handler it is given.
 */
const hls = vi.hoisted(() => ({
  errorHandler: null as ((event: string, data: { fatal: boolean; type: string }) => void) | null,
  recoverMediaError: vi.fn(),
  startLoad: vi.fn(),
}))

vi.mock('hls.js', () => {
  class FakeHls {
    static Events = { ERROR: 'hlsError' }
    static ErrorTypes = { NETWORK_ERROR: 'networkError', MEDIA_ERROR: 'mediaError' }
    static isSupported = () => true
    on(_event: string, handler: (event: string, data: { fatal: boolean; type: string }) => void) {
      hls.errorHandler = handler
    }
    loadSource() {}
    attachMedia() {}
    destroy() {}
    startLoad() {
      hls.startLoad()
    }
    recoverMediaError() {
      hls.recoverMediaError()
    }
  }
  return { default: FakeHls }
})

vi.mock('@/api/endpoints', () => ({
  catalogApi: { get: vi.fn(), episode: vi.fn() },
  libraryApi: { asset: vi.fn() },
  playbackApi: { start: vi.fn(), progress: vi.fn(), stop: vi.fn(), stopOnUnload: vi.fn() },
}))

function anAssetDetail(overrides: Partial<MediaAssetDetail> = {}): MediaAssetDetail {
  return {
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
    ...overrides,
  }
}

function aTicket(overrides: Partial<PlaybackTicket> = {}): PlaybackTicket {
  return {
    sessionId: 'session-1',
    method: 'DirectPlay',
    streamUrl: '/api/playback/sessions/session-1/stream',
    resumePositionTicks: 0,
    selection: { audio: null, subtitle: null },
    plan: {
      method: 'DirectPlay',
      transcodeReasons: [],
      decisions: [],
      backend: 'Software',
      accelerationReasons: [],
      decodeAccelerated: false,
    },
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(libraryApi.asset).mockResolvedValue(anAssetDetail())
  vi.mocked(catalogApi.get).mockResolvedValue(aWork({ id: 'work-1', kind: 'Movie', title: 'A Title' }))
  vi.mocked(playbackApi.progress).mockResolvedValue(undefined)
  vi.mocked(playbackApi.stop).mockResolvedValue(undefined)
})

describe('PlayerPage headings and controls', () => {
  it('PlayerPage_names_the_page_with_a_heading_instead_of_an_unstructured_line_of_text', async () => {
    // Arrange — this route has no PageHeader; without a heading a screen reader gets no statement
    // of what is on screen.
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())

    // Act
    renderWithProviders(<PlayerPage />)

    // Assert
    expect(await screen.findByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Source code (AGPLv3+)' })).toBeInTheDocument()
  })

  it('PlayerPage_says_what_the_playback_method_control_does_rather_than_only_naming_the_method', async () => {
    // Arrange — the control announced "DirectPlay, button", which names the state but not the action.
    const user = userEvent.setup()
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    renderWithProviders(<PlayerPage />)

    // Act
    const disclosure = await screen.findByRole('button', { name: 'Why DirectPlay?' })
    await user.click(disclosure)

    // Assert — expanded, and pointing at the panel it actually reveals.
    expect(disclosure).toHaveAttribute('aria-expanded', 'true')
    const panelId = disclosure.getAttribute('aria-controls')
    expect(panelId).toBeTruthy()
    expect(document.getElementById(panelId ?? '')).toBeInTheDocument()
  })
})

describe('PlayerPage', () => {
  it('PlayerPage_offers_a_retry_that_refetches_after_a_session_fails_to_start', async () => {
    // Arrange — the session never starts; the viewer must not be stranded on a bare error.
    vi.mocked(playbackApi.start)
      .mockRejectedValueOnce(new ApiError(502, 'playback.start_failed', 'The sidecar could not be reached.'))
      .mockResolvedValueOnce(aTicket())

    // Act
    renderWithProviders(<PlayerPage />)

    // Assert — the narrowed message reaches the viewer, with a way forward.
    expect(await screen.findByText('The sidecar could not be reached.')).toBeInTheDocument()
    const retry = screen.getByRole('button', { name: 'Retry' })
    expect(screen.getByRole('link', { name: /back to/i })).toBeInTheDocument()

    // Act — retry
    await userEvent.click(retry)

    // Assert — a second start attempt was made and the stranded state clears.
    await waitFor(() => expect(playbackApi.start).toHaveBeenCalledTimes(2))
    expect(await screen.findByLabelText('Video player')).toBeInTheDocument()
    expect(screen.queryByText('The sidecar could not be reached.')).not.toBeInTheDocument()
  })

  it('PlayerPage_names_the_audio_track_the_session_plays_by_its_language_when_the_ticket_lists_it', async () => {
    // Arrange — the session plays stream 2, and the ticket says what stream 2 is.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        selection: { audio: 2, subtitle: null },
        media: {
          audioTracks: [
            { index: 1, language: 'eng', codec: 'aac', channels: 2, isDefault: true },
            { index: 2, language: 'spa', codec: 'ac3', channels: 6, isDefault: false },
          ],
          subtitleTracks: [],
          qualities: [{ id: 'original', maxWidth: null, maxHeight: null, maxBitrateKbps: null }],
          quality: 'original',
          streamOffsetTicks: 0,
          durationTicks: null,
        },
      }),
    )

    // Act
    renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why DirectPlay?' }))

    // Assert
    expect(await screen.findByText('Spanish · 5.1 · Dolby Digital')).toBeInTheDocument()
  })

  it('PlayerPage_labels_an_audio_track_it_cannot_name_by_number_and_an_unselected_one_as_default', async () => {
    // Arrange — an older ticket without the track list, then one where the server used its default.
    vi.mocked(playbackApi.start).mockResolvedValueOnce(aTicket({ selection: { audio: 2, subtitle: null } }))
    const first = renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why DirectPlay?' }))
    expect(await screen.findByText('Track 2')).toBeInTheDocument()
    first.unmount()

    vi.mocked(playbackApi.start).mockResolvedValueOnce(aTicket({ selection: { audio: null, subtitle: null } }))
    renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why DirectPlay?' }))

    // Assert
    expect(await screen.findByText('Default')).toBeInTheDocument()
  })

  it('PlayerPage_explains_which_encoder_a_transcode_used_and_why', async () => {
    // Arrange — the backend chose a hardware encoder and said why; the panel must relay that
    // reasoning rather than leaving the viewer to infer it from the method badge alone.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        method: 'Transcode',
        plan: {
          method: 'Transcode',
          transcodeReasons: ["video codec 'hevc' not supported"],
          decisions: [],
          backend: 'Vaapi',
          accelerationReasons: ["source codec 'hevc' is also decoded on 'Vaapi', avoiding a software decode"],
          decodeAccelerated: true,
        },
      }),
    )

    // Act
    renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why Transcode?' }))

    // Assert
    expect(await screen.findByText('Vaapi · hardware')).toBeInTheDocument()
    expect(screen.getByText(/is also decoded on 'Vaapi'/)).toBeInTheDocument()
  })

  it('PlayerPage_lists_what_a_transcode_produces_in_the_why_panel', async () => {
    // Arrange — HEVC out, HDR brought to SDR, a picture subtitle drawn in, surround folded to stereo.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        method: 'Transcode',
        plan: {
          method: 'Transcode',
          transcodeReasons: ['HDR video needs tone mapping on this device'],
          decisions: [],
          backend: 'Software',
          accelerationReasons: [],
          decodeAccelerated: false,
          outputCodec: 'Hevc',
          toneMapped: true,
          burnInSubtitleIndex: 4,
          audioChannels: 2,
        },
      }),
    )

    // Act
    renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why Transcode?' }))

    // Assert — each term is paired with its value in the description list.
    const panel = await screen.findByText('Why Transcode?', { selector: 'p' })
    const list = panel.parentElement?.querySelector('dl')
    if (!list) throw new Error('expected the why panel to carry its description list')
    const pairs = Array.from(list.querySelectorAll('dt')).map((term) => [term.textContent, term.nextElementSibling?.textContent])
    expect(pairs).toEqual(
      expect.arrayContaining([
        ['Video codec', 'HEVC'],
        ['Dynamic range', 'HDR → SDR'],
        ['Subtitles', 'Burned in'],
        ['Audio channels', 'Stereo'],
      ]),
    )
  })

  it('PlayerPage_shows_an_older_ticket_without_the_produced_rows', async () => {
    // Arrange — a session planned before codec, tone mapping, burn-in and channels were recorded.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({
        method: 'Transcode',
        plan: {
          method: 'Transcode',
          transcodeReasons: ["video codec 'hevc' not supported"],
          decisions: [],
          backend: 'Software',
          accelerationReasons: [],
          decodeAccelerated: false,
        },
      }),
    )

    // Act
    renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why Transcode?' }))

    // Assert
    expect(await screen.findByText('Audio track')).toBeInTheDocument()
    for (const term of ['Video codec', 'Dynamic range', 'Subtitles', 'Audio channels']) {
      expect(screen.queryByText(term)).not.toBeInTheDocument()
    }
  })

  it('PlayerPage_keeps_playing_the_same_session_when_the_live_stream_reconnects', async () => {
    // Arrange — a reconnect re-reads what the stream may have missed. Opening a session is not a
    // read: re-running it started a second session and the film began again from the resume point.
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    renderWithProviders(
      <RealtimeProvider>
        <PlayerPage />
      </RealtimeProvider>,
    )
    expect(await screen.findByLabelText('Video player')).toBeInTheDocument()
    const stream = vi.mocked(openRealtimeStream).mock.lastCall?.[0]
    expect(stream?.onResync).toBeDefined()

    // Act
    act(() => stream?.onResync?.())

    // Assert — the title lookup is re-read like any other read, and the session is left alone.
    await waitFor(() => expect(libraryApi.asset).toHaveBeenCalledTimes(2))
    expect(playbackApi.start).toHaveBeenCalledTimes(1)
    expect(playbackApi.stop).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Video player')).toBeInTheDocument()
  })

  it('PlayerPage_stops_the_session_on_pagehide_without_putting_the_token_in_a_url', async () => {
    // Arrange — sendBeacon cannot carry a header, so it used to send the token as ?access_token=.
    const beacon = vi.fn()
    Object.defineProperty(navigator, 'sendBeacon', { value: beacon, configurable: true })
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())
    renderWithProviders(<PlayerPage />)
    expect(await screen.findByLabelText('Video player')).toBeInTheDocument()

    // Act
    act(() => {
      window.dispatchEvent(new Event('pagehide'))
    })

    // Assert
    expect(playbackApi.stopOnUnload).toHaveBeenCalledWith(aTicket().sessionId)
    expect(beacon).not.toHaveBeenCalled()
  })

  it('PlayerPage_says_a_refusal_for_a_full_server_is_about_now_not_about_the_title', async () => {
    // Arrange — the server is converting as many streams as it may; the title itself is fine.
    vi.mocked(playbackApi.start).mockRejectedValue(
      new ApiError(429, 'playback.transcode_limit', 'The server is converting as many streams as it can right now.'),
    )

    // Act
    renderWithProviders(<PlayerPage />)

    // Assert
    expect(await screen.findByText('No stream is free right now')).toBeInTheDocument()
    expect(screen.getByText('The server is converting as many streams as it can right now.')).toBeInTheDocument()
    expect(screen.queryByText("This title can't be played")).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('PlayerPage_shows_no_encoder_section_for_a_direct_play_that_never_encodes', async () => {
    // Direct Play re-encodes nothing, so an "Encoder" heading there would be a claim about work
    // that never happened.
    vi.mocked(playbackApi.start).mockResolvedValue(aTicket())

    renderWithProviders(<PlayerPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Why DirectPlay?' }))

    expect(await screen.findByText(/plays as-is on this device/)).toBeInTheDocument()
    expect(screen.queryByText('Encoder')).not.toBeInTheDocument()
  })

  it('PlayerPage_leaves_a_dead_hls_stream_for_the_retry_screen_instead_of_a_black_player', async () => {
    // Arrange — hls.js reports a playlist that stopped answering on its own event, not on <video>.
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({ method: 'Transcode', streamUrl: '/api/playback/sessions/session-1/hls/manifest.m3u8' }),
    )
    hls.errorHandler = null
    renderWithProviders(<PlayerPage />)
    await waitFor(() => expect(hls.errorHandler).not.toBeNull())

    hls.startLoad.mockClear()

    // Act — a non-fatal hiccup, then a fatal one that gets one reload, then a second fatal one.
    act(() => hls.errorHandler?.('hlsError', { fatal: false, type: 'networkError' }))
    act(() => hls.errorHandler?.('hlsError', { fatal: true, type: 'networkError' }))
    expect(hls.startLoad).toHaveBeenCalledTimes(1)
    expect(screen.getByLabelText('Video player')).toBeInTheDocument()
    act(() => hls.errorHandler?.('hlsError', { fatal: true, type: 'networkError' }))

    // Assert
    expect(await screen.findByText('Playback stopped')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument()
  })

  it('PlayerPage_tries_to_recover_a_fatal_decode_error_once_before_giving_up', async () => {
    vi.mocked(playbackApi.start).mockResolvedValue(
      aTicket({ method: 'Transcode', streamUrl: '/api/playback/sessions/session-1/hls/manifest.m3u8' }),
    )
    hls.errorHandler = null
    hls.recoverMediaError.mockClear()
    renderWithProviders(<PlayerPage />)
    await waitFor(() => expect(hls.errorHandler).not.toBeNull())

    act(() => hls.errorHandler?.('hlsError', { fatal: true, type: 'mediaError' }))
    expect(hls.recoverMediaError).toHaveBeenCalledTimes(1)
    expect(screen.getByLabelText('Video player')).toBeInTheDocument()

    act(() => hls.errorHandler?.('hlsError', { fatal: true, type: 'mediaError' }))

    expect(await screen.findByText('Playback stopped')).toBeInTheDocument()
    expect(hls.recoverMediaError).toHaveBeenCalledTimes(1)
  })
})
