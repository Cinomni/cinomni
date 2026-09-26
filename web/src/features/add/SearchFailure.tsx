import { Link } from 'react-router'
import { ApiError } from '@/lib/api'
import { EmptyState } from '@/ui/EmptyState'

/** The server's code for a search no provider could be asked to answer (see `MetadataErrors.NoProvider`). */
const NO_PROVIDER = 'metadata.no_provider'

/** Where the provider keys are entered: the Metadata card of the console's settings page. */
const METADATA_SETTINGS = '/console/settings#settings-metadata'

/**
 * A failed metadata search, told apart by cause. A provider nobody configured is not a failed request:
 * the server names which provider is missing and why, and an administrator is sent to where its key
 * goes. Anything else is a request that did not get through and is worth retrying.
 */
export function SearchFailure({ error, isAdministrator }: { error: unknown; isAdministrator: boolean }) {
  if (error instanceof ApiError && error.code === NO_PROVIDER) {
    return (
      <EmptyState
        title="No metadata provider configured"
        description={error.message}
        action={
          isAdministrator ? (
            <p className="mx-auto max-w-md text-meta text-muted">
              Enter the key under{' '}
              <Link to={METADATA_SETTINGS} className="font-medium text-accent underline-offset-2 hover:underline">
                Settings &gt; Metadata
              </Link>
              . It applies at once, with no restart.
            </p>
          ) : undefined
        }
      />
    )
  }

  return (
    <EmptyState
      title="Search failed"
      description="The metadata providers could not be reached. Try again in a moment."
    />
  )
}
