import { describe, expect, it } from 'vitest'
import { aWork } from '@/test/factories'
import { backdropSources } from './artwork'

describe('backdropSources', () => {
  it('backdropSources_offers_each_rendition_by_width_and_defaults_to_the_large_one', () => {
    const work = aWork({
      backdropUrl: 'https://image.tmdb.org/t/p/original/b.jpg',
      backdropSmallUrl: 'https://image.tmdb.org/t/p/w780/b.jpg',
      backdropLargeUrl: 'https://image.tmdb.org/t/p/w1280/b.jpg',
    })

    expect(backdropSources(work)).toEqual({
      src: 'https://image.tmdb.org/t/p/w1280/b.jpg',
      srcSet:
        'https://image.tmdb.org/t/p/w780/b.jpg 780w, ' +
        'https://image.tmdb.org/t/p/w1280/b.jpg 1280w, ' +
        'https://image.tmdb.org/t/p/original/b.jpg 3840w',
    })
  })

  it('uses the original alone when the provider has no smaller renditions', () => {
    const original = 'https://artworks.thetvdb.com/banners/fanart/1.jpg'
    const work = aWork({ backdropUrl: original, backdropSmallUrl: original, backdropLargeUrl: original })

    expect(backdropSources(work)).toEqual({ src: original })
  })

  it('has nothing to show without a backdrop', () => {
    expect(backdropSources(aWork({ backdropUrl: null }))).toBeNull()
  })
})
