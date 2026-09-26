import type { QueryClient } from '@tanstack/react-query'

/** The merged entry list every enabled catalog source publishes. */
export const indexerCatalogQueryKey = ['indexer-catalog']

/** The administrator's catalog sources. Deliberately not nested under the entry key above. */
export const catalogSourcesQueryKey = ['indexer-catalog-sources']

/**
 * Adding, refreshing, renaming, toggling or removing a source changes both the source list and the
 * entries the catalog offers, so every source mutation refreshes the two together.
 */
export function invalidateCatalogSources(queryClient: QueryClient): Promise<unknown> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: catalogSourcesQueryKey }),
    queryClient.invalidateQueries({ queryKey: indexerCatalogQueryKey }),
  ])
}
