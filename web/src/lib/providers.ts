import type { ExternalIdDto, MetadataCandidate } from '@/api/types'

/**
 * Provider source name (as the Metadata module reports it, lower-case) → the Catalog enum member.
 * A candidate whose provider is missing here can be added but never refreshed, because Metadata has
 * no external id to go back to — so every source the Metadata module registers must appear.
 */
export const PROVIDER_ENUM: Record<string, ExternalIdDto['provider']> = {
  tmdb: 'Tmdb',
  tvdb: 'Tvdb',
  imdb: 'Imdb',
  tvmaze: 'TvMaze',
}

/**
 * The provider sources the Metadata module can refresh a work from, keyed by the Catalog enum member.
 * IMDb is deliberately absent: it is an identity the platform stores, not a source it can fetch.
 */
const REFRESH_SOURCES: Partial<Record<ExternalIdDto['provider'], string>> = {
  Tmdb: 'tmdb',
  Tvdb: 'tvdb',
  TvMaze: 'tvmaze',
}

/** The first of a work's external ids that Metadata can go back to, or null if none of them is one. */
export function refreshSourceFor(
  externalIds: readonly ExternalIdDto[],
): { provider: string; externalId: string } | null {
  for (const external of externalIds) {
    const provider = REFRESH_SOURCES[external.provider]
    if (provider) return { provider, externalId: external.value }
  }
  return null
}

/**
 * Every identity a candidate is known by: the provider it came from, plus the cross-references the
 * search merged it on. Series candidates are de-duplicated across providers, so the row the user
 * picked usually carries ids from two or three of them and the work should be added with all of
 * them — otherwise a later refresh from another provider creates a second work.
 */
export function externalIdsOf(candidate: MetadataCandidate): ExternalIdDto[] {
  const byProvider = new Map<ExternalIdDto['provider'], string>()

  const add = (provider: ExternalIdDto['provider'] | undefined, value: string | null) => {
    if (provider && value && !byProvider.has(provider)) byProvider.set(provider, value)
  }

  // The picked row wins: it is the provider whose title and artwork the user actually saw.
  add(PROVIDER_ENUM[candidate.provider.toLowerCase()], candidate.externalId)
  add('Tvdb', candidate.tvdbId)
  add('Imdb', candidate.imdbId)
  add('Tmdb', candidate.tmdbId)

  return [...byProvider].map(([provider, value]) => ({ provider, value }))
}
