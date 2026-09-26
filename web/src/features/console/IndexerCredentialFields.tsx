import type { IndexerProtocol, SetIndexerCredentialRequest } from '@/api/types'
import { Segmented, type SegmentedOption } from '@/ui/Segmented'
import { TextField } from '@/ui/TextField'

/** How an indexer authenticates: not at all, with an API key, or with an account. */
export type IndexerAuthKind = 'none' | 'apiKey' | 'login'

export interface IndexerCredentialDraft {
  kind: IndexerAuthKind
  username: string
  secret: string
}

const AUTH_LABELS: Record<IndexerAuthKind, string> = {
  none: 'None',
  apiKey: 'API key',
  login: 'Username & password',
}

/**
 * The kinds a protocol can use. A `Definition` indexer only ever signs in with an account — there
 * is no key parameter in a scraped site — while Torznab/Newznab take either an API key or, behind
 * an authenticating proxy, a username and password.
 */
export function authKindsFor(protocol: IndexerProtocol, allowNone: boolean): IndexerAuthKind[] {
  const kinds: IndexerAuthKind[] = protocol === 'Definition' ? ['login'] : ['apiKey', 'login']
  return allowNone ? ['none', ...kinds] : kinds
}

/** The draft to start from, keeping a kind the protocol still offers and falling back otherwise. */
export function draftFor(
  protocol: IndexerProtocol,
  allowNone: boolean,
  previous?: IndexerCredentialDraft,
): IndexerCredentialDraft {
  const kinds = authKindsFor(protocol, allowNone)
  const kind = previous && kinds.includes(previous.kind) ? previous.kind : (kinds[0] ?? 'none')
  return { kind, username: previous?.username ?? '', secret: previous?.secret ?? '' }
}

/** The wire body for a draft, or null when it asks for no credential at all. */
export function toCredentialRequest(draft: IndexerCredentialDraft): SetIndexerCredentialRequest | null {
  if (draft.kind === 'none') return null
  const username = draft.kind === 'login' && draft.username.trim() !== '' ? draft.username.trim() : null
  return { secret: draft.secret, username }
}

interface IndexerCredentialFieldsProps {
  protocol: IndexerProtocol
  draft: IndexerCredentialDraft
  onChange: (next: IndexerCredentialDraft) => void
  allowNone: boolean
  autoFocus?: boolean
}

/**
 * The credential inputs shared by adding an indexer and replacing its credential. Write-only: the
 * secret field always starts empty, because a stored secret is never read back.
 */
export function IndexerCredentialFields({ protocol, draft, onChange, allowNone, autoFocus }: IndexerCredentialFieldsProps) {
  const kinds = authKindsFor(protocol, allowNone)
  const options: SegmentedOption[] = kinds.map((kind) => ({ value: kind, label: AUTH_LABELS[kind] }))
  // Basic auth needs a user-id; a login form may not (some sites sign in with a password alone).
  const usernameRequired = protocol !== 'Definition'

  return (
    <div className="flex flex-col gap-4">
      {kinds.length > 1 && (
        <Segmented
          label="Authentication"
          options={options}
          value={draft.kind}
          onChange={(value) => onChange({ ...draft, kind: kinds.find((kind) => kind === value) ?? draft.kind })}
        />
      )}

      {draft.kind === 'login' && (
        <TextField
          label="Username"
          autoComplete="off"
          required={usernameRequired}
          value={draft.username}
          onChange={(event) => onChange({ ...draft, username: event.target.value })}
          hint={
            protocol === 'Definition'
              ? 'The account this indexer signs in as.'
              : 'Sent as HTTP Basic authentication, so the base URL must be https.'
          }
        />
      )}

      {draft.kind !== 'none' && (
        <TextField
          label={draft.kind === 'login' ? 'Password' : 'API key'}
          type="password"
          autoComplete="off"
          required
          value={draft.secret}
          onChange={(event) => onChange({ ...draft, secret: event.target.value })}
          autoFocus={autoFocus}
        />
      )}
    </div>
  )
}
