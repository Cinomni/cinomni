# Indexer catalog source format

Cinomni does not host, provide or index any content, and it ships no list of sites. The indexer
catalog in the console is empty until an administrator subscribes to a **catalog source**: an https
URL, chosen by the administrator, that publishes a JSON manifest in the format below. Each entry in
the manifest is a declarative indexer definition that can then be installed with one click.

This page is the reference for whoever writes such a manifest. Adding and managing sources is
described in [DEPLOYMENT.md](../DEPLOYMENT.md), section 5.

## The manifest

```json
{
  "schemaVersion": 1,
  "name": "Example catalog",
  "entries": [
    {
      "key": "example-public",
      "version": 1,
      "name": "Example Public Indexer",
      "description": "A public indexer for the example.org test site.",
      "protocol": "Definition",
      "releaseProtocol": "Torrent",
      "baseUrls": ["https://indexer.example.org/", "https://mirror.example.org/"],
      "requiresFlareSolverr": false,
      "defaultPriority": 25,
      "defaultSettings": {
        "minimumSeeders": 1,
        "preferMagnet": true,
        "queryLimit": null,
        "grabLimit": null,
        "limitsUnit": "Day",
        "useFlareSolverr": false
      },
      "definition": {
        "schemaVersion": 1,
        "resultKind": "Torrent",
        "search": {
          "requests": [
            {
              "contentKinds": ["Movie", "Series", "Season", "Episode"],
              "method": "Get",
              "urlTemplate": "/search?q={{term}}"
            }
          ],
          "responseFormat": "Html",
          "rows": { "selector": "table.results tbody tr", "maxRows": 100 },
          "fields": {
            "title": { "selector": "td.name a", "attribute": "Text", "transform": "Trim" },
            "downloadUrl": { "selector": "a[href^='magnet:']", "attribute": "Href", "transform": "Trim" },
            "sizeBytes": { "selector": "td.size", "attribute": "Text", "transform": "ParseSize" },
            "seeders": { "selector": "td.seeds", "attribute": "Text", "transform": "ParseInt" }
          }
        }
      }
    }
  ]
}
```

| Member | Required | Rule |
|---|---|---|
| `schemaVersion` | yes | Must be `1`. |
| `name` | no | At most 200 characters. Informational only. |
| `entries` | yes | An array of at most 250 entries. An empty array is valid. |
| `entries[].key` | yes | 1 to 50 characters of `a-z`, `0-9`, `.`, `_`, `-`, starting with a letter or digit. Unique within the manifest. |
| `entries[].version` | yes | A positive integer. Raise it when the definition changes. |
| `entries[].name` | yes | 1 to 200 printable characters. |
| `entries[].description` | no | At most 1000 printable characters. |
| `entries[].protocol` | yes | `Definition`. |
| `entries[].releaseProtocol` | yes | `Torrent`, and the definition's `resultKind` must agree. |
| `entries[].baseUrls` | yes | 1 to 10 absolute http(s) URLs with a public host and no user info, each at most 1000 characters. The first is the default; an installed indexer may only point at one of these origins. |
| `entries[].requiresFlareSolverr` | no | Default `false`. When `true`, `defaultSettings.useFlareSolverr` must be `true` too, and the installed indexer cannot turn FlareSolverr off while the entry says so. |
| `entries[].defaultPriority` | no | 1 to 50; default 25. |
| `entries[].defaultSettings` | no | `minimumSeeders` ≥ 0, `queryLimit` and `grabLimit` > 0 or null, `limitsUnit` `Day` (the only unit today); the booleans default to `false`. |
| `entries[].definition` | yes | A declarative definition object, exactly as `POST /api/discovery/indexer-definitions` accepts it (validated with strict selectors). It must **not** declare a `session` login block. |

Unknown members are ignored, so a later format can add fields without breaking older installations.

## How Cinomni treats a manifest

A manifest is untrusted input from a third party:

- **https only.** The source URL must be an absolute https URL with a public host, no user info and
  no fragment. A host that resolves to a loopback, private, link-local or metadata address is refused
  when the connection is made.
- **Bounded.** One GET, no redirects followed (publish the final URL), a 30 second timeout and a
  4 MiB cap on the body, counted as it arrives. The body must be UTF-8.
- **All or nothing.** If any entry breaks any rule above, the whole manifest is rejected with
  `discovery.catalog_source.invalid_manifest`, and the message names the entry and the rule. The
  source keeps the entries of its last valid manifest; nothing is half-applied.
- **Snapshotted.** The last valid manifest is stored in the database, so the catalog survives a
  restart and the source being down. Refreshing is manual (`Refresh` in the console, or
  `POST /api/discovery/indexer-catalog/sources/{id}/refresh`).
- **No logins.** A catalog entry never ships a login. A definition for a site that needs an account
  is uploaded by the administrator who holds that account.

Installing an entry copies its definition into the indexer. A later refresh, disabling the source or
removing it never changes or removes an installed indexer; only the catalog listing changes.

## Refresh outcome codes

| Code | Meaning |
|---|---|
| `discovery.catalog_source.refreshed` | The manifest was fetched and every entry is valid. |
| `discovery.catalog_source.fetch_failed` | The source could not be reached, timed out, redirected or answered a non-2xx status. |
| `discovery.catalog_source.too_large` | The body is larger than 4 MiB. |
| `discovery.catalog_source.invalid_manifest` | The body is not UTF-8 JSON in this format, or an entry breaks a rule. |
| `discovery.catalog_source.invalid_url` | The stored URL no longer passes the https/public-host rule. |
