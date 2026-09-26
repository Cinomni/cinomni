import { useQuery } from '@tanstack/react-query'
import { catalogApi, playbackApi } from '@/api/endpoints'
import type { MediaAssetSummary, Work } from '@/api/types'
import { MediaHero } from '@/components/media/MediaHero'
import { primaryActionFor } from '@/components/media/primaryAction'
import { WorkAvailability, WorkEyebrow } from '@/components/media/WorkFacts'
import { seriesKeys } from '@/features/series/useSeries'
import { workPath } from '@/lib/routes'
import { ButtonLink } from '@/ui/Button'
import { ArrowRightIcon, PlayIcon } from '@/ui/icons'
import type { ContinueItem } from './useHomeData'

/**
 * The home page's featured title, with the action that fits where this viewer stands with it: resume
 * where they stopped, play the next episode, play the film, or — when nothing is on disk yet — read
 * about it. Every one of those facts comes from the server; the hero only picks which to lead with.
 */
export function HomeHero({
  work,
  assets,
  continueItems,
}: {
  work: Work
  assets: readonly MediaAssetSummary[]
  continueItems: readonly ContinueItem[]
}) {
  const isSeries = work.kind === 'Series'
  // The list route leaves the synopsis out; the detail route has it, for every viewer. Shared with the
  // title's own page, so opening it next is already loaded. An enrichment: a failed read only drops it.
  const detail = useQuery({ queryKey: ['work', work.id], queryFn: () => catalogApi.get(work.id), retry: false })
  const overview = detail.data?.overview ?? null

  const nextUp = useQuery({
    queryKey: seriesKeys.nextUp(work.id),
    queryFn: async () => (await playbackApi.nextUp(work.id)) ?? null,
    enabled: isSeries,
  })

  const playableAssetId = isSeries
    ? null
    : (assets.find((asset) => asset.workId === work.id && asset.state === 'Active')?.id ??
      assets.find((asset) => asset.workId === work.id)?.id ??
      null)
  const resume = continueItems.find((item) => item.work.id === work.id)

  const action = primaryActionFor({
    work,
    playableAssetId,
    resumePositionTicks: resume?.assetId === playableAssetId ? resume?.positionTicks : null,
    nextUp: nextUp.data,
  })

  return (
    <MediaHero
      work={work}
      size="home"
      headingLevel="h2"
      eyebrow={
        <div className="flex flex-wrap items-center gap-3">
          <span className="text-label uppercase text-accent">
            {work.hasAsset || work.availableEpisodeCount > 0 ? 'Ready to watch' : 'Just added'}
          </span>
          <WorkEyebrow work={work} />
        </div>
      }
      overview={overview}
      status={<WorkAvailability work={work} />}
      actions={
        <>
          <ButtonLink
            to={action.href}
            size="lg"
            icon={action.kind === 'details' ? undefined : <PlayIcon className="size-5" />}
          >
            {action.label}
            {action.kind === 'details' && <ArrowRightIcon className="size-4" />}
          </ButtonLink>
          {action.kind !== 'details' && (
            <ButtonLink to={workPath(work)} size="lg" variant="overlay">
              Details
            </ButtonLink>
          )}
        </>
      }
    />
  )
}
