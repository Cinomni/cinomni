import { cn } from '@/lib/cn'

/**
 * Where a release came from and how healthy its swarm is, as the indexer reported it. Seeders are what
 * decide whether a torrent finishes at all, so they read as a fact of their own rather than faint
 * metadata: none at all is shown as a warning, and an indexer that does not say is said to not say.
 */
export function ReleaseSource({
  indexerName,
  seeders,
  leechers,
  className,
}: {
  indexerName?: string | null
  seeders?: number | null
  leechers?: number | null
  className?: string
}) {
  const reported = seeders != null || leechers != null
  return (
    <p className={cn('flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs', className)}>
      {indexerName && <span className="font-medium text-fg">{indexerName}</span>}
      {seeders != null && (
        <span className={seeders > 0 ? 'text-success' : 'text-danger'}>{seeders} seeders</span>
      )}
      {leechers != null && <span className="text-muted">{leechers} leechers</span>}
      {!reported && <span className="text-faint">Peers not reported</span>}
    </p>
  )
}
