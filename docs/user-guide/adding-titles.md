# Adding movies and series

Adding a title is how an administrator tells Cinomni "get this". A member uses the same screens to
[request](requests.md) a title instead.

## Add a movie or a series

1. Choose **Add a movie** or **Add a series** on **Home** or in **Movies** / **Series**, or type the
   name in search (**Ctrl K**) and pick *Add a movie called …*.
2. Search by title (and optionally year). Results come from the metadata providers: TMDB for movies;
   TheTVDB, TMDB and TVmaze for series. If no provider is configured, the page says so; see
   [First run](../getting-started/first-run.md#3-connect-your-sources).
3. Pick the right result and add it, with monitoring on if you want Cinomni to fetch it.

The title appears in the library at once, with its artwork. It becomes playable when a file has been
downloaded and imported, which you can follow on **Activity**.

## Monitoring

A **monitored** title is searched for automatically: when it is added, when an episode airs, and
periodically afterwards until it reaches the quality its profile aims for. An unmonitored title
stays in the library and is never searched for on its own.

A series can be monitored in several ways, chosen from **Monitoring** on its page:

| Choice | Watches |
|---|---|
| All episodes | Every episode of every season. |
| Future episodes | Only episodes that have not aired yet. |
| Existing episodes | Only episodes that have already aired. |
| First season | Only the first real season (specials do not count). |
| Latest season | Only the highest-numbered season, usually the one airing now. |
| Pilot only | Only S01E01, to try a show before committing to it. |
| Nothing | Stop watching the show. |

Seasons and single episodes can also be switched on and off individually. The page tells you how many
episodes a change affects before you apply it.

New episodes are searched for after they air, plus the **Search delay after air** set by the
administrator (**Console → Settings → Monitoring**).

## Searching by hand

On a title's page, **Find releases** runs an interactive search across every indexer and lists what
was found, with the verdict of the title's profile on each release and the reasons behind it:

- **Grab** downloads a release the profile accepts.
- A release the profile rejected can still be taken, but Cinomni asks you to confirm
  (**Download anyway**), and records that it was a deliberate override.
- **Block release** stops a release from ever being picked again for that title. Blocked releases are
  listed, and can be unblocked, under **Console → Profiles**.

See [Profiles and decisions](../administration/profiles-and-decisions.md).

## Artwork

Cinomni picks a poster and a backdrop automatically. To use another one, open the title and choose
artwork from the candidates the providers offer.

## Removing a title

**Remove** takes the title out of the catalog and stops everything in flight for it. By default the
files stay on disk; tick **Also delete the files from disk** to remove them as well. This cannot be
undone.
