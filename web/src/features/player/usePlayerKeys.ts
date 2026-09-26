import { useEffect, useRef } from 'react'

export interface PlayerKeyHandlers {
  onTogglePlay: () => void
  onSkip: (deltaSeconds: number) => void
  onVolume: (delta: number) => void
  onToggleMute: () => void
  onToggleFullscreen: () => void
  /** Absent when there are no subtitles to turn on. */
  onToggleSubtitles?: () => void
  /** Present while something can be closed. */
  onEscape?: () => void
  /** Any handled key counts as the viewer being there: the controls come back. */
  onActivity: () => void
}

const SKIP_SECONDS = 10
const VOLUME_STEP = 0.1

/**
 * Whether a key belongs to the element that has focus rather than to the player: typing in a field,
 * moving a slider, or pressing a focused button with Space or Enter.
 */
function ownedByFocus(event: KeyboardEvent): boolean {
  const target = event.target
  if (!(target instanceof HTMLElement)) return false
  // A slider (the timeline, the volume) moves with the arrows and paging keys; the rest stay the player's.
  if (target instanceof HTMLInputElement && target.type === 'range') {
    return ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(event.key)
  }
  if (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return true
  return target.tagName === 'BUTTON' && (event.key === ' ' || event.key === 'Enter')
}

/**
 * The player's keyboard shortcuts, the ones video players share: Space or K to play and pause, the
 * arrows to skip and change the volume, J and L to skip, M to mute, F for full screen, C for subtitles,
 * Escape to close the settings.
 */
export function usePlayerKeys(handlers: PlayerKeyHandlers): void {
  // Read through a ref so the listener is attached once, not on every render.
  const ref = useRef(handlers)
  ref.current = handlers

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return
      const h = ref.current
      if (event.key === 'Escape') {
        if (h.onEscape) {
          event.preventDefault()
          h.onEscape()
        }
        return
      }
      if (ownedByFocus(event)) return

      const actions: Record<string, (() => void) | undefined> = {
        ' ': h.onTogglePlay,
        k: h.onTogglePlay,
        ArrowLeft: () => h.onSkip(-SKIP_SECONDS),
        j: () => h.onSkip(-SKIP_SECONDS),
        ArrowRight: () => h.onSkip(SKIP_SECONDS),
        l: () => h.onSkip(SKIP_SECONDS),
        ArrowUp: () => h.onVolume(VOLUME_STEP),
        ArrowDown: () => h.onVolume(-VOLUME_STEP),
        m: h.onToggleMute,
        f: h.onToggleFullscreen,
        c: h.onToggleSubtitles,
      }
      const action = actions[event.key.length === 1 ? event.key.toLowerCase() : event.key]
      if (!action) return
      event.preventDefault()
      action()
      h.onActivity()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])
}
