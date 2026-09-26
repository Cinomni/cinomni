import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { metadataApi } from '@/api/endpoints'
import type { MediaKind, MetadataCandidate } from '@/api/types'

export interface UseMetadataSearchResult {
  term: string
  setTerm: (value: string) => void
  year: string
  setYear: (value: string) => void
  /** Form submit handler: guards against an empty term and triggers the search mutation. */
  submit: (event: FormEvent) => void
  candidates: MetadataCandidate[] | undefined
  isPending: boolean
  isError: boolean
  /** Why the last search failed, or null: a provider nobody configured and a failed request read differently. */
  error: unknown
}

/**
 * Search term/year state and the metadata-provider search mutation shared by the movie and series
 * add pages. The two pages differ in what a result becomes (an added work vs. a filed request) and
 * where it routes to, so this hook stops at "here are the candidates" and leaves picking one to the
 * caller.
 */
export function useMetadataSearch(kind: MediaKind, initialTerm = ''): UseMetadataSearchResult {
  const [term, setTerm] = useState(initialTerm)
  const [year, setYear] = useState('')

  const search = useMutation({
    mutationFn: () => metadataApi.search(term.trim(), year ? Number(year) : null, kind),
  })

  // Arriving with a term (the global search hands one over as `?q=`) runs that search once, so the
  // person lands on results rather than on a field holding what they already typed.
  const ranInitial = useRef(false)
  useEffect(() => {
    if (ranInitial.current || !initialTerm.trim()) return
    ranInitial.current = true
    search.mutate()
  }, [initialTerm, search])

  function submit(event: FormEvent) {
    event.preventDefault()
    if (term.trim()) search.mutate()
  }

  return {
    term,
    setTerm,
    year,
    setYear,
    submit,
    candidates: search.data,
    isPending: search.isPending,
    isError: search.isError,
    error: search.error,
  }
}
