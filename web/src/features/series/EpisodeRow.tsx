import { useState } from 'react'
import { useNavigate } from 'react-router'
import type { Episode, MediaAssetSummary, MonitoredTarget } from '@/api/types'
import { InteractiveSearchDialog } from '@/features/search/InteractiveSearchDialog'
import { cn } from '@/lib/cn'
import { formatAirDate, formatEpisodeCode, isUnaired } from '@/lib/format'
import { MediaStatus } from '@/components/media/MediaStatus'
import { episodeAvailabilityLabel, mediaStateFromEpisode, type EpisodeAvailability } from '@/lib/status'
import { CheckIcon, PlayIcon, SearchIcon } from '@/ui/icons'
import { Menu } from '@/ui/Menu'
import { Switch } from '@/ui/Switch'
import { useSetEpisodeMonitored } from './useSeries'

/** What the row reports about the episode: on disk, wanted but absent, or not broadcast yet. */
export function availabilityOfEpisode(episode: Episode): EpisodeAvailability {
  if (episode.hasAsset) return 'InLibrary'
  return isUnaired(episode.airDate, episode.airDateTime) ? 'Unaired' : 'Missing'
}

/**
 * One episode of a season: its code, title and air date, whether it is in the library, and — for an
 * administrator — a monitoring toggle. Everyone else sees the state without a control the API would
 * refuse them.
 */
export function EpisodeRow({
  workId,
  episode,
  target,
  asset,
  watched = false,
  canManage,
}: {
  workId: string
  episode: Episode
  target: MonitoredTarget | null
  asset?: MediaAssetSummary
  watched?: boolean
  canManage: boolean
}) {
  const navigate = useNavigate()
  const [searching, setSearching] = useState(false)
  const setMonitored = useSetEpisodeMonitored(workId, episode.seasonNumber)

  const availability = availabilityOfEpisode(episode)
  const code = formatEpisodeCode(episode.seasonNumber, episode.number)
  const airDate = formatAirDate(episode.airDate)

  return (
    <li className="group/row -mx-3 flex items-center gap-3 rounded-control px-3 py-2 transition-colors hover:bg-surface sm:gap-4">
      {/* Play leads the row: it is the one action everyone has, and it sits where the eye starts. */}
      {asset ? (
        <button
          onClick={() => navigate(`/watch/${asset.id}`)}
          aria-label={`Play ${code}`}
          className="grid size-9 shrink-0 place-items-center rounded-full bg-elevated text-fg transition-colors hover:bg-accent hover:text-on-accent"
        >
          <PlayIcon className="ml-0.5 size-4" />
        </button>
      ) : (
        <span aria-hidden="true" className="size-9 shrink-0" />
      )}

      <span className="w-14 shrink-0 font-mono text-meta text-faint">{code}</span>

      <div className="min-w-0 flex-1">
        <p className={cn('truncate text-card', watched ? 'text-muted' : 'text-fg')}>
          {episode.title ?? 'Untitled episode'}
          {watched && (
            <>
              <CheckIcon className="ml-1.5 inline size-3.5 text-success" />
              <span className="sr-only"> — watched</span>
            </>
          )}
        </p>
        {airDate && <p className="mt-0.5 text-meta tabular-nums text-faint">{airDate}</p>}
      </div>

      <MediaStatus
        state={mediaStateFromEpisode[availability]}
        label={episodeAvailabilityLabel[availability]}
        className="shrink-0"
      />

      {canManage && target && (
        <>
          <Switch
            checked={target.monitored}
            disabled={setMonitored.isPending}
            label={`Monitor ${code}`}
            onChange={(monitored) => setMonitored.mutate({ targetId: target.id, monitored })}
          />
          <Menu
            label={`Actions for ${code}`}
            size="icon-sm"
            items={[{ label: 'Find releases', icon: <SearchIcon />, onSelect: () => setSearching(true) }]}
          />
          <InteractiveSearchDialog
            targetId={target.id}
            title={code}
            open={searching}
            onClose={() => setSearching(false)}
          />
        </>
      )}
    </li>
  )
}
