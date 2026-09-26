# Profiles and decisions

Every release an indexer returns is judged against an **acquisition profile** before anything is
downloaded. The judgement is stored with its reasons, so the question "why did it take *this* one?"
always has an answer.

## The default profiles

Cinomni creates two profiles on first start: **Default** for movies and **Default (Series)** for
series. Both accept these qualities, ranked from best to worst:

| Rank | Quality |
|---|---|
| 60 | Blu-ray 2160p |
| 55 | WEB-DL 2160p |
| 50 | Blu-ray 1080p |
| 45 | WEB-DL 1080p |
| 40 | WEBRip 1080p |
| 30 | Blu-ray 720p |
| 25 | WEB-DL 720p |
| 15 | HDTV 1080p |
| 10 | HDTV 720p |

On top of the quality, a few formats add to a release's score: a *Remux* scores higher, *HEVC* a
little, and for series a *Season Pack* ranks above a single episode of the same quality. The series
profile ignores files under 50 MB (samples and trailers) and sets no maximum size, so season packs are
never rejected for being large.

## The cutoff

The **cutoff rank** is the quality at which a title is *good enough*. The default is 50 (Blu-ray
1080p).

- Below the cutoff, Cinomni keeps looking, and a new release must beat what is on disk.
- Once the file on disk reaches the cutoff, the title is no longer searched for.
- The cutoff stops the *search*; it does not cap the quality. If the first acceptable release a
  search finds is 2160p, it is taken.

## Upgrades

With **Search for upgrades** on, a file below the cutoff is replaced when a better release turns up.
The replaced file is moved to the recycle folder, never deleted (see
[Import and storage](import-and-storage.md#the-recycle-folder)). With it off, a file that is already
there stays as it is.

Switching upgrades on does not replace anything at once; it lets future searches act on files already
in the library.

Change both settings under **Console → Profiles**.

## Reading a decision

On a title's page (and in **Activity**) you can see every release that was evaluated:

- whether it was **accepted** or **rejected**;
- the reasons: quality not allowed, too small, below what is on disk, blocked, and so on;
- its score and the formats that contributed to it.

## Choosing by hand

**Find releases** on a title runs an interactive search and shows the same verdicts:

- **Grab** takes an accepted release.
- **Download anyway** takes a rejected release after a confirmation; the override is recorded.
- **Block release** stops that release from being chosen again for that title, automatically or by
  hand, until it is unblocked. Blocked releases are listed under **Console → Profiles**, with the
  reason, and **Unblock** lifts the block.

## Keeping the history small

Evaluations are kept for the period set by **Evaluation retention** in **Console → Settings → Decision**
and then purged. A block's reason is kept even after the evaluation that led to it is purged.
