import { useEffect, useRef, useState } from 'react'

/**
 * Tracks whether a horizontally-scrollable element is currently scrolled away from its start and/or
 * end edge. Callers use it to fade or disable the side that has nothing more behind it — a static fade
 * or an always-enabled arrow would mislead once the user has actually scrolled all the way to that side.
 */
export function useScrollEdges<T extends HTMLElement>() {
  const ref = useRef<T | null>(null)
  const [atStart, setAtStart] = useState(true)
  const [atEnd, setAtEnd] = useState(true)

  useEffect(() => {
    const el = ref.current
    if (!el) return undefined

    function measure() {
      if (!el) return
      setAtStart(el.scrollLeft <= 1)
      setAtEnd(el.scrollLeft + el.clientWidth >= el.scrollWidth - 1)
    }

    measure()
    el.addEventListener('scroll', measure, { passive: true })
    window.addEventListener('resize', measure)
    return () => {
      el.removeEventListener('scroll', measure)
      window.removeEventListener('resize', measure)
    }
  }, [])

  return { ref, atStart, atEnd }
}
