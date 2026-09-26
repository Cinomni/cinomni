import type { Work } from '@/api/types'

export type HeroArtwork = Pick<
  Work,
  'title' | 'kind' | 'posterUrl' | 'posterSmallUrl' | 'backdropUrl' | 'backdropSmallUrl' | 'backdropLargeUrl'
>

/** Widths the server's renditions are cut to (see `backdropSmallUrl` / `backdropLargeUrl`). */
const BACKDROP_SMALL_WIDTH = 780
const BACKDROP_LARGE_WIDTH = 1280
/** Providers' originals run 1920–3840 px; described generously so only a wide, dense screen picks it. */
const BACKDROP_ORIGINAL_WIDTH = 3840

/**
 * The backdrop as a responsive image: a phone loads the 780 px rendition, a laptop the 1280 px one, and
 * only a screen wider than that the original. A provider without renditions answers its original for
 * every size, and then a single `src` is all there is to say.
 */
export function backdropSources(work: HeroArtwork): { src: string; srcSet?: string } | null {
  const original = work.backdropUrl
  if (!original) return null
  const small = work.backdropSmallUrl ?? original
  const large = work.backdropLargeUrl ?? original
  if (small === original && large === original) return { src: original }
  return {
    src: large,
    srcSet: [
      `${small} ${BACKDROP_SMALL_WIDTH}w`,
      `${large} ${BACKDROP_LARGE_WIDTH}w`,
      `${original} ${BACKDROP_ORIGINAL_WIDTH}w`,
    ].join(', '),
  }
}
