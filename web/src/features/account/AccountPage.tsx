import { useAuth } from '@/auth/useAuth'
import { PageHeader } from '@/components/PageHeader'
import { Badge } from '@/ui/Badge'
import { Card } from '@/ui/Card'
import { TwoFactorSection } from './TwoFactorSection'

/**
 * What each person can change about their own account, as opposed to what an administrator changes
 * about everyone's. The console's Users page is the second thing and refuses a member outright; this
 * is the first, so every account reaches it.
 *
 * Today that is the second factor and nothing else. Role and permissions are shown but not offered:
 * an account granting itself a capability is exactly what the administrator policy exists to stop,
 * and rendering a control the API would refuse would be inviting the refusal.
 */
/**
 * What this account's request cap is, in the words its three states actually deserve.
 *
 * `null` is the awkward one and it is left deliberately vague: it means the installation's own
 * default applies, and `/me` does not send that number. Naming a figure here would mean inventing
 * one. Saying which case you are in is still worth more than silence — someone who is capped
 * individually deserves to know before they hit it, not after.
 */
function ceilingSentence(permissions: { contentCeiling?: string | null; contentCeilingRegion?: string | null; contentCeilingApplies?: boolean } | undefined): string {
  if (!permissions?.contentCeiling) {
    return 'Nothing is hidden from you by age classification.'
  }
  if (!permissions.contentCeilingApplies) {
    return `A ceiling of ${permissions.contentCeiling} was set for ${permissions.contentCeilingRegion ?? 'another region'}, and it is not applied while this installation classifies differently.`
  }
  return `Titles classified above ${permissions.contentCeiling} are hidden. A title with no classification stays visible.`
}

function requestLimitSentence(limit: number | null): string {
  if (limit === null) {
    return 'How many requests you can have open at once follows this installation’s own setting.'
  }
  if (limit === 0) {
    return 'There is no cap on how many requests you can have open at once.'
  }
  return `You can have ${limit} ${limit === 1 ? 'request' : 'requests'} open at once. A request stops counting once it is rejected or the title arrives.`
}

export function AccountPage() {
  const { user, refreshUser } = useAuth()

  return (
    <>
      <PageHeader title="Your account" subtitle="Settings that apply to you, on this installation." />

      <div className="space-y-4">
        <Card as="section" className="space-y-3">
          <h2 className="font-medium text-fg">Signed in as</h2>
          <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div>
              <dt className="text-faint">Username</dt>
              <dd className="text-fg">{user?.username ?? 'Unknown'}</dd>
            </div>
            <div>
              <dt className="text-faint">Role</dt>
              <dd>
                <Badge tone={user?.isAdministrator ? 'accent' : 'neutral'}>
                  {user?.isAdministrator ? 'Administrator' : 'Member'}
                </Badge>
              </dd>
            </div>
          </dl>
          <p className="text-xs text-faint">
            Your role and what it lets you do are set by an administrator, not from here.
          </p>
        </Card>

        {/* Only if this account may ask for titles at all — a cap on an account that cannot request
            is a number with nothing to count, and stating it would just be confusing. */}
        {user?.permissions.canRequest && (
          <Card as="section" className="space-y-2">
            <h2 className="font-medium text-fg">Requests</h2>
            <p className="text-sm text-muted">{requestLimitSentence(user.permissions.openRequestLimit)}</p>
            {user.permissions.requestsAutoApproved && (
              <p className="text-sm text-muted">Your requests are approved as soon as you submit them.</p>
            )}
          </Card>
        )}

        <Card as="section" className="space-y-2">
          <h2 className="font-medium text-fg">What you can watch</h2>
          <p className="text-sm text-muted">{ceilingSentence(user?.permissions)}</p>
        </Card>

        <TwoFactorSection enabled={user?.twoFactorEnabled ?? false} onChanged={refreshUser} />
      </div>
    </>
  )
}
