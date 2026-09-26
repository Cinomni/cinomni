import type { Work } from '@/api/types'
import { Alert } from '@/ui/Alert'

/**
 * Tells an administrator why members cannot see this title yet. Only an administrator ever reads a
 * held work — the API hides it from everyone else — so the notice needs no role check of its own.
 */
export function AwaitingMetadataNotice({ work }: { work: Work }) {
  if (!work.awaitingMetadata) {
    return null
  }

  return (
    <Alert tone="info" title="Hidden from members for now" className="mt-4">
      Collection rules cannot read this title&apos;s genres or rating until its metadata arrives, so it stays
      hidden until then. If it does not arrive, use Refresh metadata from the menu.
    </Alert>
  )
}
