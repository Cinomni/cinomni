/** .NET DateTime ticks are 100-nanosecond intervals: 10,000,000 per second. */
const TICKS_PER_SECOND = 10_000_000

export const ticksToSeconds = (ticks: number): number => ticks / TICKS_PER_SECOND
export const secondsToTicks = (seconds: number): number => Math.round(seconds * TICKS_PER_SECOND)

/** Format a byte count as a compact human size (e.g. "1.4 GB"). */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  const value = bytes / 1024 ** exponent
  return `${value.toFixed(value >= 100 || exponent === 0 ? 0 : 1)} ${units[exponent]}`
}

/** Format a bytes-per-second rate (e.g. "3.2 MB/s"). */
export const formatRate = (bytesPerSecond: number): string =>
  bytesPerSecond <= 0 ? '—' : `${formatBytes(bytesPerSecond)}/s`

/** Format a duration in seconds as h:mm:ss or m:ss. */
export function formatDuration(totalSeconds: number): string {
  if (!Number.isFinite(totalSeconds) || totalSeconds < 0) return '0:00'
  const seconds = Math.floor(totalSeconds % 60)
  const minutes = Math.floor((totalSeconds / 60) % 60)
  const hours = Math.floor(totalSeconds / 3600)
  const paddedSeconds = seconds.toString().padStart(2, '0')
  if (hours > 0) {
    return `${hours}:${minutes.toString().padStart(2, '0')}:${paddedSeconds}`
  }
  return `${minutes}:${paddedSeconds}`
}

/** Runtime in minutes → "2h 16m". */
export function formatRuntime(minutes: number | null): string | null {
  if (minutes == null || minutes <= 0) return null
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return hours > 0 ? `${hours}h ${rest}m` : `${rest}m`
}

/** Season and episode numbers are padded to at least this width, so S01E02 sorts and scans cleanly. */
const EPISODE_CODE_DIGITS = 2

/**
 * Season/episode numbers as the canonical `S01E02` code. Numbers wider than two digits keep all their
 * digits (`S01E123`), and a season with no episode number renders as just `S01`.
 */
export function formatEpisodeCode(seasonNumber: number, episodeNumber?: number | null): string {
  const pad = (value: number) => Math.trunc(Math.abs(value)).toString().padStart(EPISODE_CODE_DIGITS, '0')
  const season = `S${pad(seasonNumber)}`
  return episodeNumber == null ? season : `${season}E${pad(episodeNumber)}`
}

/**
 * A provider air date (`YYYY-MM-DD`, a calendar date and not an instant) as a short local date.
 * Parsed field-by-field on purpose: `new Date('2026-07-28')` is UTC midnight and renders as the
 * previous day west of Greenwich.
 */
export function formatAirDate(airDate: string | null): string | null {
  if (!airDate) return null
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(airDate)
  if (!match) return airDate
  const [, year, month, day] = match
  const date = new Date(Number(year), Number(month) - 1, Number(day))
  return Number.isNaN(date.getTime()) ? airDate : date.toLocaleDateString(undefined, { dateStyle: 'medium' })
}

/**
 * Whether an episode has not been broadcast yet — one that simply does not exist to download. The
 * timezone-aware instant is preferred when the provider supplied one; the calendar date is the
 * fallback, and an episode with no date at all is not treated as unaired.
 */
export function isUnaired(airDate: string | null, airDateTime?: string | null): boolean {
  if (airDateTime) {
    const instant = new Date(airDateTime).getTime()
    if (!Number.isNaN(instant)) return instant > Date.now()
  }
  if (!airDate) return false
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(airDate)
  if (!match) return false
  const [, year, month, day] = match
  return new Date(Number(year), Number(month) - 1, Number(day)).getTime() > Date.now()
}

/** A percentage 0..100 from a 0..1 fraction. */
export const asPercent = (fraction: number): number => Math.round(Math.min(1, Math.max(0, fraction)) * 100)

/** Format an ISO timestamp as a short local date-time. */
export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

/** Format an ISO timestamp as a compact relative time ("just now", "5m ago", "3h ago", "2d ago"). */
export function formatRelative(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return iso
  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000))
  if (seconds < 45) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.round(hours / 24)
  if (days < 7) return `${days}d ago`
  return formatDateTime(iso)
}
