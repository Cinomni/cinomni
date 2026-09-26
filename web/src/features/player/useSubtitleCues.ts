import { useQuery } from '@tanstack/react-query'
import { playbackApi } from '@/api/endpoints'
import { parseVtt, type Cue } from './vtt'

const NO_CUES: Cue[] = []

/**
 * The cues of the subtitle track at `url`, fetched with the session's header and parsed once. A track
 * that fails to load reads as no cues plus the error, so the film keeps playing without them.
 */
export function useSubtitleCues(url: string | null) {
  const query = useQuery({
    queryKey: ['playback', 'subtitles', url],
    queryFn: async ({ signal }) => parseVtt(await playbackApi.subtitles(url ?? '', signal)),
    enabled: url !== null,
    staleTime: Infinity,
    retry: 1,
    // A converted track belongs to the session that served it; nothing else reads it again.
    gcTime: 5 * 60_000,
  })
  return { cues: query.data ?? NO_CUES, isLoading: query.isFetching, isError: query.isError }
}
