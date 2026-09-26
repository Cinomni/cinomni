import { describe, expect, it, vi } from 'vitest'
import { SESSION_EXPIRED_EVENT } from './api'
import { openRealtimeStream, parseFrame } from './realtime'
import { tokenStore } from './token'

/**
 * The stream is untrusted input like any other response, and this is the only place it becomes data.
 * A frame that cannot be understood must be dropped, never guessed at: the alternative is writing
 * `undefined` into a cache entry a page is rendering from.
 */
describe('parseFrame', () => {
  it('parseFrame_reads_a_topic_only_signal', () => {
    // Arrange
    const frame = 'data: {"topic":"downloads","payload":null}'

    // Act
    const message = parseFrame(frame)

    // Assert
    expect(message).toEqual({ topic: 'downloads', payload: null })
  })

  it('parseFrame_reads_a_payload_carrying_snapshot', () => {
    const frame = 'data: {"topic":"downloads.progress","payload":[{"id":"a","progress":0.5}]}'

    const message = parseFrame(frame)

    expect(message?.topic).toBe('downloads.progress')
    expect(message?.payload).toEqual([{ id: 'a', progress: 0.5 }])
  })

  it('parseFrame_joins_a_multi_line_data_field', () => {
    // SSE splits a long payload across data: lines; joining them is the spec, not a nicety.
    const frame = 'data: {"topic":\ndata: "activity"}'

    expect(parseFrame(frame)).toEqual({ topic: 'activity', payload: undefined })
  })

  it('parseFrame_ignores_the_heartbeat', () => {
    // The keep-alive comment is what stops a proxy reaping an idle stream; it carries nothing.
    expect(parseFrame(': ping')).toBeNull()
  })

  it('parseFrame_drops_malformed_json', () => {
    expect(parseFrame('data: {not json')).toBeNull()
  })

  it('parseFrame_drops_a_frame_with_no_topic', () => {
    // Without a topic there is nothing to invalidate, and a truthy object would be worse than nothing.
    expect(parseFrame('data: {"payload":[1,2,3]}')).toBeNull()
    expect(parseFrame('data: {"topic":42}')).toBeNull()
    expect(parseFrame('data: "just a string"')).toBeNull()
  })
})

describe('openRealtimeStream on a token another tab replaced', () => {
  it('openRealtimeStream_reconnects_with_the_new_token_instead_of_ending_the_new_session', async () => {
    // Arrange — the stream's token is refused because another tab signed in again meanwhile.
    tokenStore.set('old-token')
    const expired = vi.fn()
    window.addEventListener(SESSION_EXPIRED_EVENT, expired)
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(async () => {
        tokenStore.set('new-token')
        return new Response(null, { status: 401 })
      })
      .mockImplementation(async () => new Response(new ReadableStream(), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    // Act
    const stop = openRealtimeStream({ onMessage: () => undefined })
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    stop()

    // Assert
    const [, init] = fetchMock.mock.calls[1] as [string, RequestInit]
    expect(init.headers).toMatchObject({ Authorization: 'Bearer new-token' })
    expect(tokenStore.get()).toBe('new-token')
    expect(expired).not.toHaveBeenCalled()
    window.removeEventListener(SESSION_EXPIRED_EVENT, expired)
    tokenStore.clear()
    vi.unstubAllGlobals()
  })
})
