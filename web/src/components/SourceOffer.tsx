/** A modified distribution can point every screen at its own corresponding source at build time. */
export function SourceOffer({ commit }: { commit?: string | null }) {
  const configured = import.meta.env.VITE_SOURCE_URL?.trim()
  let href = commit
    ? `https://github.com/Cinomni/cinomni/tree/${commit}`
    : 'https://github.com/Cinomni/cinomni'

  if (configured) {
    try {
      const url = new URL(configured)
      if (url.protocol === 'https:' || url.protocol === 'http:') href = url.href
    } catch {
      // Ignore a malformed build-time override and retain the upstream fallback.
    }
  }

  return <a className="underline hover:text-fg" href={href}>Source code (AGPLv3+)</a>
}
