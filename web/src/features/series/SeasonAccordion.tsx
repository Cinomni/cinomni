import { useId, useState } from 'react'
import type { MediaAssetSummary } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { MediaStatus } from '@/components/media/MediaStatus'
import { InteractiveSearchDialog } from '@/features/search/InteractiveSearchDialog'
import { Button } from '@/ui/Button'
import { ChevronRightIcon, SearchIcon } from '@/ui/icons'
import { Menu } from '@/ui/Menu'
import { Skeleton } from '@/ui/Skeleton'
import { Switch } from '@/ui/Switch'
import { EpisodeRow } from './EpisodeRow'
import {
  useSeasonEpisodes,
  useSeasonProgress,
  useSeasonSearch,
  useSetSubtreeMonitored,
  type SeasonWithTargets,
} from './useSeries'

/**
 * One collapsible season. Its episodes are fetched the first time it is opened and not before: a
 * long-running show has hundreds of them across twenty seasons, and rendering the series page must
 * not depend on loading all of them.
 */
export function SeasonAccordion({
  workId,
  season: { season, target, targetByEpisodeId },
  assetByUnit,
  canManage,
  defaultOpen = false,
}: {
  workId: string
  season: SeasonWithTargets
  assetByUnit?: ReadonlyMap<string, MediaAssetSummary>
  canManage: boolean
  defaultOpen?: boolean
}) {
  const panelId = useId()
  const [expanded, setExpanded] = useState(defaultOpen)
  const [searching, setSearching] = useState(false)

  const episodes = useSeasonEpisodes(workId, season.number, expanded)
  const setSubtree = useSetSubtreeMonitored(workId)
  const search = useSeasonSearch(workId)

  const assetIds = (episodes.data ?? [])
    .map((episode) => assetByUnit?.get(episode.id)?.id)
    .filter((assetId): assetId is string => !!assetId)
  const progress = useSeasonProgress(workId, season.number, assetIds)

  const heading = season.number === 0 ? 'Specials' : `Season ${season.number}`
  const inLibrary = target ? target.totalCount - target.missingCount : 0

  const fraction = target && target.totalCount > 0 ? inLibrary / target.totalCount : 0

  return (
    <section className="border-t border-line-soft">
      <div className="flex items-center gap-3">
        <button
          onClick={() => setExpanded((open) => !open)}
          aria-expanded={expanded}
          aria-controls={panelId}
          className="group flex min-w-0 flex-1 items-center gap-4 py-4 text-left"
        >
          <ChevronRightIcon
            className={cn('size-4 shrink-0 text-muted transition-transform duration-150', expanded && 'rotate-90')}
          />
          <span className="min-w-0 flex-1">
            <span className="block truncate text-body font-semibold text-fg group-hover:text-accent-strong">{heading}</span>
            <span className="mt-1 flex items-center gap-3 text-meta tabular-nums text-faint">
              <span>
                {target
                  ? `${inLibrary} of ${target.totalCount} in library`
                  : season.expectedEpisodeCount != null
                    ? `${season.expectedEpisodeCount} episodes`
                    : 'Not monitored'}
              </span>
              {target && target.totalCount > 0 && (
                // The season's share on disk, as the same thin line the posters use for progress.
                <span aria-hidden="true" className="hidden h-0.75 w-24 overflow-hidden rounded-full bg-line sm:block">
                  <span className="block h-full rounded-full bg-success/80" style={{ width: `${fraction * 100}%` }} />
                </span>
              )}
            </span>
          </span>
        </button>

        {target && target.missingCount > 0 && (
          <MediaStatus state="missing" label={`${target.missingCount} missing`} className="hidden sm:inline-flex" />
        )}

        {/* Both controls need a target: there is nothing to search for or toggle until the policy has
            opened one for this season, and the API answers 404 either way. */}
        {canManage && target && (
          <>
            <Switch
              checked={target.monitored}
              disabled={setSubtree.isPending}
              label={`Monitor ${heading} and its episodes`}
              onChange={(monitored) => setSubtree.mutate({ targetId: target.id, monitored })}
            />
            <Menu
              label={`Actions for ${heading}`}
              size="icon-sm"
              items={[
                {
                  label: `Search ${heading} automatically`,
                  icon: <SearchIcon />,
                  disabled: search.isPending,
                  onSelect: () => search.mutate(season.number),
                },
                // The other half of searching: see what is out there and choose it yourself.
                { label: 'Choose a release…', onSelect: () => setSearching(true) },
              ]}
            />
          </>
        )}
      </div>

      {/* Both header actions are one click with no page of their own to report back on. A search that
          was refused used to read as a bare "Search failed" with no reason, and a monitoring toggle
          that was refused reported nothing at all — the switch simply snapped back on the next read. */}
      {search.isError && (
        <p role="alert" className="pb-3 pl-8 text-meta text-danger">
          {errorMessage(search.error, `${heading} could not be searched.`)}
        </p>
      )}
      {setSubtree.isError && (
        <p role="alert" className="pb-3 pl-8 text-meta text-danger">
          {errorMessage(setSubtree.error, `Monitoring for ${heading} could not be changed.`)}
        </p>
      )}

      {canManage && target && (
        <InteractiveSearchDialog
          targetId={target.id}
          title={heading}
          open={searching}
          onClose={() => setSearching(false)}
        />
      )}

      <div id={panelId} hidden={!expanded}>
        {!expanded ? null : episodes.isPending ? (
          <div role="status" aria-label={`Loading ${heading}`} className="space-y-2 pb-4">
            <Skeleton className="h-10" />
            <Skeleton className="h-10" />
            <Skeleton className="h-10" />
          </div>
        ) : episodes.isError ? (
          // Deliberately not styled like the empty state below it: "could not be loaded" and "none
          // have been synced" were the same grey sentence, and only one of them is worth retrying.
          <div className="flex flex-col items-start gap-2 pb-5 pl-8">
            <p role="alert" className="text-meta text-danger">
              {errorMessage(episodes.error, `${heading}’s episodes could not be loaded.`)}
            </p>
            <Button size="sm" variant="subtle" onClick={() => void episodes.refetch()}>
              Retry
              <span className="sr-only"> loading {heading}</span>
            </Button>
          </div>
        ) : episodes.data.length === 0 ? (
          <p className="pb-5 pl-8 text-meta text-muted">
            No episodes have been synced for this season yet.
          </p>
        ) : (
          <ul className="pb-4">
            {episodes.data.map((episode) => {
              const asset = assetByUnit?.get(episode.id)
              return (
                <EpisodeRow
                  key={episode.id}
                  workId={workId}
                  episode={episode}
                  target={targetByEpisodeId.get(episode.id) ?? null}
                  asset={asset}
                  watched={asset ? (progress.data?.get(asset.id)?.played ?? false) : false}
                  canManage={canManage}
                />
              )
            })}
          </ul>
        )}
      </div>
    </section>
  )
}
