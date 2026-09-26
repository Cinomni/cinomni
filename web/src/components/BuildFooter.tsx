import { useQuery } from '@tanstack/react-query'
import { systemApi } from '@/api/endpoints'
import { SourceOffer } from './SourceOffer'

/** One shared cache entry: the console's build panel reads the same answer this footer does. */
export const systemInfoQueryKey = ['system', 'info']

/**
 * Which build the household is looking at, in the quietest form the layout has.
 *
 * It exists for one exchange: a member reports something odd, and whoever is helping them asks what
 * version they are on. That is why it is not administrator-only, and why it says
 * `informationalVersion` rather than the plain number — the suffixed string is the one that
 * identifies a build without ambiguity.
 *
 * A failed read hides the version, but the source link remains available. A stamped build links to
 * its exact revision; an unstamped development build links to the upstream repository.
 */
export function BuildFooter() {
  // The server reads its own build identity once at startup and it cannot change while the process
  // runs, so this is fetched once per session rather than kept fresh.
  const { data } = useQuery({
    queryKey: systemInfoQueryKey,
    queryFn: ({ signal }) => systemApi.info(signal),
    staleTime: Infinity,
    retry: false,
  })

  return (
    <footer className="break-all px-4 pb-6 text-center text-xs text-faint sm:px-6 lg:px-8">
      {data && <>Cinomni {data.informationalVersion} · </>}
      <SourceOffer commit={data?.commit} />
    </footer>
  )
}
