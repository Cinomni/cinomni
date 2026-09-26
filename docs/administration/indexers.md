# Indexers

An indexer is a site or service Cinomni asks for releases. Cinomni ships with no indexers configured:
you choose your own sources, under **Console → Indexers**.

## Three kinds

| Kind | Use it for |
|---|---|
| **From a catalog source** | Sites a catalog source you subscribed to has a definition for. Pick one from the catalog on the Indexers page and fill in what it asks for. |
| **Torznab / Newznab** | Any service that speaks one of these APIs, including an indexer proxy you already run. |
| **Definition** | A site that speaks neither. You upload a declarative definition (JSON) describing how to search it and read its results. |

Only torrents can be downloaded: Usenet is not supported, even from a Newznab indexer.

## Adding a Torznab or Newznab indexer

**Add indexer**, then:

| Field | What to enter |
|---|---|
| Name | How it appears in results and reasons. |
| Protocol | Torznab or Newznab. |
| Base URL | The API address the service gives you. |
| API key | If the service needs one. Stored encrypted; requires `CINOMNI_SECRET_KEY`. |
| Movie categories / TV categories | The category numbers to search in. |
| Supports movie search / TV search | Whether to ask it for each kind. |
| Movie / TV search params | The parameter names it accepts (for example an IMDb or TVDB id), comma-separated. |
| Priority | The tie-break when the same release comes from several indexers: the lower number wins. Indexers can also be switched off without being removed. |

## Indexers from a catalog source

The catalog is empty until you add a catalog source (**Catalog sources → Add**): a name and an https
URL publishing a manifest of definitions ([format](../indexer-catalog-format.md)). Pick an entry from
it. Depending on the site, it asks for:

- the site address, when there are several;
- credentials (an API key, or a username and password for a site you sign in to), stored encrypted;
- **Minimum seeders**: releases reporting fewer are ignored (blank allows any);
- **Daily query limit** and **Daily detail/grab limit**, to stay within what the site allows (blank is
  unlimited);
- **Use FlareSolverr**, for a site behind a browser challenge;
- **Prefer magnet links**, when the site offers both a magnet and a `.torrent` file.

## Definitions

Under **Indexer definitions**, **Upload indexer definition** accepts a JSON document. Before saving it
you can paste a sample response the site would return and preview what the definition extracts; this
never contacts the real site. Then add an indexer with protocol **Definition** and select it.

## Sites behind a browser challenge

Some sites put a browser check in front of their pages. For those, Cinomni can use **FlareSolverr**, an
isolated headless browser shipped in the compose file. It has no port on your host and can reach the
internet only through a proxy that refuses addresses on your own network. Switch it on per indexer
with **Use FlareSolverr**.

## Credentials

A stored secret is never shown again. To change it, enter a new one; saving it signs in again on the
next search. Removing an indexer also removes its stored credential and session.

## Safety

Cinomni refuses to contact an indexer whose address resolves to your own machine or local network
(loopback, private and link-local addresses). An indexer proxy running on the same host must therefore
be reached by a public name, or run where its address is not private. See
[SECURITY.md](../../SECURITY.md#outbound-http-indexers-subtitles-metadata-torrent-links).

## When searches find nothing

- Check that the indexer supports the kind you are searching for (movie or TV) and has the right
  categories.
- Run **Find releases** on a title to see, per indexer, what came back and why each release was
  accepted or rejected.
- **Console → Operations** shows failed commands with their reason.
