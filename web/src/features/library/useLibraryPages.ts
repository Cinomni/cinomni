import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { catalogApi } from '@/api/endpoints'
import type { WorkKind, WorkPageQuery } from '@/api/types'

/** Titles per request: a few screens of posters, small enough that the first paint is quick. */
export const LIBRARY_PAGE_SIZE = 60

/** How long typing must pause before the title search goes to the server. */
export const SEARCH_DEBOUNCE_MS = 250

/** The value once it has stopped changing for `delayMs`; the first value immediately. */
export function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), delayMs)
    return () => window.clearTimeout(timer)
  }, [value, delayMs])
  return settled
}

/**
 * The library a page at a time. Filtering and ordering happen on the server, so what loads is only
 * what is shown; each further page is asked for as the viewer reaches the end of the grid. The previous
 * answer stays on screen while a changed filter loads, so the grid does not flash empty.
 */
export function useLibraryPages(query: WorkPageQuery) {
  const pages = useInfiniteQuery({
    queryKey: ['works', 'page', query],
    queryFn: ({ pageParam, signal }) => catalogApi.page(query, pageParam, LIBRARY_PAGE_SIZE, signal),
    initialPageParam: 0,
    getNextPageParam: (last) => {
      const next = last.offset + last.items.length
      return last.items.length > 0 && next < last.total ? next : undefined
    },
    placeholderData: keepPreviousData,
  })

  const works = useMemo(() => pages.data?.pages.flatMap((page) => page.items) ?? [], [pages.data])
  const total = pages.data?.pages[0]?.total ?? 0
  return { pages, works, total }
}

/** Counts for the filter controls: kinds on the shelf, and the genres of the kind shown. */
export function useLibraryFacets(collectionId: string | null, kind: WorkKind | undefined) {
  return useQuery({
    queryKey: ['works', 'facets', collectionId, kind ?? null],
    queryFn: ({ signal }) => catalogApi.facets(collectionId ?? undefined, kind, signal),
    placeholderData: keepPreviousData,
  })
}
