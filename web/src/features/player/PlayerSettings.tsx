import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { PlaybackMedia } from '@/api/types'
import { cn } from '@/lib/cn'
import { CheckIcon, ChevronLeftIcon, ChevronRightIcon } from '@/ui/icons'
import {
  audioLabel,
  PLAYBACK_RATES,
  qualityLabel,
  rateLabel,
  subtitleLabel,
  subtitleNote,
  subtitleUnavailableReason,
} from './trackLabels'

export type SubtitleSize = 'small' | 'medium' | 'large'

export const SUBTITLE_SIZES: readonly SubtitleSize[] = ['small', 'medium', 'large']

type View = 'main' | 'audio' | 'subtitles' | 'quality' | 'speed'

export interface PlayerSettingsProps {
  id: string
  media: PlaybackMedia
  audio: number | null
  subtitle: number | null
  rate: number
  subtitleSize: SubtitleSize
  onAudio: (index: number) => void
  onSubtitle: (index: number | null) => void
  onQuality: (id: string) => void
  onRate: (rate: number) => void
  onSubtitleSize: (size: SubtitleSize) => void
}

interface Choice<T> {
  value: T
  label: string
  hint?: string | null
  disabled?: boolean
}

/** Labels made unique: two English tracks read "English · 5.1" and "English · 5.1 (2)", not twice the same. */
function distinct<T>(choices: Choice<T>[]): Choice<T>[] {
  const seen = new Map<string, number>()
  return choices.map((choice) => {
    const count = (seen.get(choice.label) ?? 0) + 1
    seen.set(choice.label, count)
    return count === 1 ? choice : { ...choice, label: `${choice.label} (${count})` }
  })
}

/**
 * The player's settings: the audio track, the subtitles, the quality and the speed, each a list of
 * radio choices under a summary row. Changing the audio or the quality opens a new session where the
 * viewer is; subtitles and speed switch in place.
 */
export function PlayerSettings(props: PlayerSettingsProps) {
  const { media, audio, subtitle, rate } = props
  const [view, setView] = useState<View>('main')
  const panelRef = useRef<HTMLDivElement>(null)

  // Each view starts with its first control focused, so the keyboard lands where the choices are.
  useEffect(() => {
    panelRef.current?.querySelector<HTMLButtonElement>('button:not([disabled])')?.focus()
  }, [view])

  const audioChoices = distinct(media.audioTracks.map((t) => ({ value: t.index, label: audioLabel(t) })))
  const subtitleChoices: Choice<number | null>[] = [
    { value: null, label: 'Off' },
    ...distinct(
      media.subtitleTracks.map((t) => ({
        value: t.index,
        label: subtitleLabel(t),
        hint: subtitleUnavailableReason(t) ?? subtitleNote(t),
        disabled: t.url === null && !t.canBurnIn,
      })),
    ),
  ]
  const qualityChoices = media.qualities.map((q) => ({ value: q.id, label: qualityLabel(q) }))
  const rateChoices = PLAYBACK_RATES.map((r) => ({ value: r, label: rateLabel(r) }))

  const summary = (choices: Choice<unknown>[], value: unknown) =>
    choices.find((c) => c.value === value)?.label ?? '—'

  return (
    <div
      id={props.id}
      ref={panelRef}
      role="dialog"
      aria-label="Playback settings"
      className="absolute bottom-full right-2 mb-2 max-h-[min(70dvh,26rem)] w-72 animate-menu-in overflow-y-auto rounded-panel border border-line bg-surface/95 p-1.5 text-sm shadow-[var(--shadow-lift)] backdrop-blur"
    >
      {view === 'main' && (
        <ul className="flex flex-col">
          {audioChoices.length > 1 && (
            <SummaryRow label="Audio" value={summary(audioChoices, audio)} onOpen={() => setView('audio')} />
          )}
          {media.subtitleTracks.length > 0 && (
            <SummaryRow label="Subtitles" value={summary(subtitleChoices, subtitle)} onOpen={() => setView('subtitles')} />
          )}
          {qualityChoices.length > 1 && (
            <SummaryRow label="Quality" value={summary(qualityChoices, media.quality)} onOpen={() => setView('quality')} />
          )}
          <SummaryRow label="Speed" value={rateLabel(rate)} onOpen={() => setView('speed')} />
        </ul>
      )}
      {view === 'audio' && (
        <ChoiceView title="Audio" onBack={() => setView('main')}>
          <Choices choices={audioChoices} value={audio} onChoose={props.onAudio} label="Audio track" />
          <Note>Switching the audio restarts the stream where you are.</Note>
        </ChoiceView>
      )}
      {view === 'subtitles' && (
        <ChoiceView title="Subtitles" onBack={() => setView('main')}>
          <Choices choices={subtitleChoices} value={subtitle} onChoose={props.onSubtitle} label="Subtitle track" />
          <div className="mt-1 border-t border-line-soft px-2 pb-1 pt-2">
            <p className="mb-1.5 text-xs text-faint" id={`${props.id}-size`}>
              Text size
            </p>
            <div role="radiogroup" aria-labelledby={`${props.id}-size`} className="grid grid-cols-3 gap-1">
              {SUBTITLE_SIZES.map((size) => (
                <button
                  key={size}
                  type="button"
                  role="radio"
                  aria-checked={props.subtitleSize === size}
                  onClick={() => props.onSubtitleSize(size)}
                  className={cn(
                    'rounded-control px-2 py-1.5 text-xs capitalize transition-colors',
                    props.subtitleSize === size ? 'bg-accent text-on-accent' : 'bg-elevated text-muted hover:text-fg',
                  )}
                >
                  {size}
                </button>
              ))}
            </div>
          </div>
        </ChoiceView>
      )}
      {view === 'quality' && (
        <ChoiceView title="Quality" onBack={() => setView('main')}>
          <Choices choices={qualityChoices} value={media.quality} onChoose={props.onQuality} label="Quality" />
          <Note>A lower quality is converted on the server to use less bandwidth.</Note>
        </ChoiceView>
      )}
      {view === 'speed' && (
        <ChoiceView title="Speed" onBack={() => setView('main')}>
          <Choices choices={rateChoices} value={rate} onChoose={props.onRate} label="Playback speed" />
        </ChoiceView>
      )}
    </div>
  )
}

function SummaryRow({ label, value, onOpen }: { label: string; value: string; onOpen: () => void }) {
  return (
    <li>
      <button
        type="button"
        onClick={onOpen}
        className="flex w-full items-center gap-3 rounded-control px-2.5 py-2 text-left transition-colors hover:bg-hover"
      >
        <span className="text-fg">{label}</span>
        <span className="ml-auto truncate text-muted">{value}</span>
        <ChevronRightIcon className="size-4 shrink-0 text-faint" />
      </button>
    </li>
  )
}

function ChoiceView({ title, onBack, children }: { title: string; onBack: () => void; children: ReactNode }) {
  return (
    <div>
      <button
        type="button"
        onClick={onBack}
        className="mb-1 flex w-full items-center gap-1.5 rounded-control px-1.5 py-2 font-medium text-fg transition-colors hover:bg-hover"
        aria-label={`Back from ${title}`}
      >
        <ChevronLeftIcon className="size-4 text-muted" />
        {title}
      </button>
      {children}
    </div>
  )
}

function Choices<T>({
  choices,
  value,
  onChoose,
  label,
}: {
  choices: Choice<T>[]
  /** Null when nothing is chosen yet — the server's default audio track, before it is named. */
  value: T | null
  onChoose: (value: T) => void
  label: string
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex flex-col">
      {choices.map((choice) => {
        const checked = choice.value === value
        return (
          <button
            key={String(choice.value)}
            type="button"
            role="radio"
            aria-checked={checked}
            disabled={choice.disabled}
            onClick={() => {
              if (!checked) onChoose(choice.value)
            }}
            className="flex w-full items-start gap-2.5 rounded-control px-2.5 py-2 text-left transition-colors hover:bg-hover disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-transparent"
          >
            <CheckIcon className={cn('mt-0.5 size-4 shrink-0 text-accent', !checked && 'invisible')} />
            <span className="flex min-w-0 flex-col">
              <span className={checked ? 'text-fg' : 'text-muted'}>{choice.label}</span>
              {choice.hint && <span className="text-xs text-faint">{choice.hint}</span>}
            </span>
          </button>
        )
      })}
    </div>
  )
}

function Note({ children }: { children: ReactNode }) {
  return <p className="px-2.5 pb-1 pt-2 text-xs text-faint">{children}</p>
}
