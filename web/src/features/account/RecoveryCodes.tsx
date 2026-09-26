import { useState } from 'react'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { Checkbox } from '@/ui/Checkbox'

/**
 * The recovery codes, at the only moment they exist anywhere they can be read. The server keeps
 * hashes; nothing can show them again, so this screen is the single chance to keep them and it says
 * so rather than assuming anyone reads a heading twice.
 *
 * The acknowledgement is a real gate, not a formality: leaving without them is the one mistake here
 * that cannot be undone from inside the app, and it costs the account its way back in.
 */
export function RecoveryCodes({ codes, onDone }: { codes: readonly string[]; onDone: () => void }) {
  const [kept, setKept] = useState(false)
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'unavailable'>('idle')

  const asText = codes.join('\n')

  async function copy() {
    try {
      // Undefined outside a secure context, which a self-hosted install reached over plain HTTP on a
      // LAN is. Reading it inside the try covers that as well as a refused permission.
      await navigator.clipboard.writeText(asText)
      setCopyState('copied')
    } catch {
      // Saying nothing would be the worst option on this particular screen: the button would look
      // like it worked, and these are the codes there is no second chance to keep.
      setCopyState('unavailable')
    }
  }

  function download() {
    const url = URL.createObjectURL(new Blob([`${asText}\n`], { type: 'text/plain' }))
    const link = document.createElement('a')
    link.href = url
    link.download = 'cinomni-recovery-codes.txt'
    // In the document and revoked on a later tick: some browsers ignore a click on a detached anchor,
    // and revoking the object URL in the same tick can cancel the download it just started.
    document.body.append(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 0)
  }

  return (
    <div className="flex flex-col gap-4">
      <Alert tone="warning" title="Save these now">
        <p>
          These ten codes are shown once and cannot be shown again. Each one signs you in a single
          time.
        </p>
        <p className="mt-1">
          They are how you get back in if you lose your authenticator — and also the only way in if
          this server loses its master key, because that key is what decrypts the shared secret your
          authenticator codes are checked against. Recovery codes do not depend on it.
        </p>
      </Alert>

      <ul className="grid grid-cols-2 gap-2 rounded-control border border-line bg-elevated p-3 font-mono text-sm">
        {codes.map((code) => (
          <li key={code} className="text-fg">
            {code}
          </li>
        ))}
      </ul>

      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="subtle" onClick={() => void copy()}>
          {copyState === 'copied' ? 'Copied' : 'Copy codes'}
        </Button>
        <Button type="button" variant="subtle" onClick={download}>
          Download as a file
        </Button>
      </div>

      {copyState === 'unavailable' && (
        <p role="alert" className="text-sm text-danger">
          This browser would not let the page copy for you — over plain HTTP it never will. Download
          the file instead, or select the codes above and copy them yourself.
        </p>
      )}

      <Checkbox
        label="I have saved these codes somewhere safe"
        checked={kept}
        onChange={(event) => setKept(event.target.checked)}
      />

      <Button type="button" disabled={!kept} onClick={onDone}>
        Done
      </Button>
    </div>
  )
}
