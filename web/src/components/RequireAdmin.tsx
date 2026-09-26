import type { ReactNode } from 'react'
import { useAuth } from '@/auth/useAuth'
import { EmptyState } from '@/ui/EmptyState'
import { SettingsIcon } from '@/ui/icons'

/**
 * Route-level gate for the operator surfaces.
 *
 * This is not a security boundary and must never be treated as one: the API's administrator policy
 * is what actually refuses a member, and every endpoint behind these pages carries it. What this
 * fixes is the experience — without it a member can reach an operator page and meet a body made
 * entirely of 403s, which reads as a broken application rather than as a surface that was never
 * theirs. It answers with a plain explanation instead.
 */
export function RequireAdmin({ children }: { children: ReactNode }) {
  const { user } = useAuth()

  if (!(user?.isAdministrator ?? false)) {
    return (
      <EmptyState
        icon={<SettingsIcon className="size-8" />}
        title="Administrators only"
        description="This page manages the platform itself. Ask an administrator of this installation if you need something from it."
      />
    )
  }

  return <>{children}</>
}
