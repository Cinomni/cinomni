/** Where the token lives. Exported so the session can follow another tab's sign-in through `storage` events. */
export const TOKEN_STORAGE_KEY = 'cinomni.session.token'

/**
 * The opaque session token issued by the Identity module (Bearer). Kept in localStorage so a reload keeps
 * the session; every request reads the current value, so a re-login is picked up immediately.
 *
 * localStorage is shared by every tab of the origin, so the token here may be newer than the one a
 * request in flight was sent with.
 */
export const tokenStore = {
  get: (): string | null => localStorage.getItem(TOKEN_STORAGE_KEY),
  set: (token: string): void => localStorage.setItem(TOKEN_STORAGE_KEY, token),
  clear: (): void => localStorage.removeItem(TOKEN_STORAGE_KEY),
  /**
   * Drops the token only if it is still the one that was rejected, and says whether it did. A 401 on
   * a token another tab has since replaced says nothing about the new session; clearing on it signed
   * that tab's fresh sign-in straight back out.
   */
  clearIfCurrent: (token: string): boolean => {
    if (localStorage.getItem(TOKEN_STORAGE_KEY) !== token) return false
    localStorage.removeItem(TOKEN_STORAGE_KEY)
    return true
  },
}
