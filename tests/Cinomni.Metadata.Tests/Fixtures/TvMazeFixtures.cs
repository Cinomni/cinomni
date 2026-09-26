namespace Cinomni.Metadata.Tests.Fixtures;

/// <summary>
/// Hand-written, deliberately synthetic TVMaze payloads. They are shaped like the real API — the same
/// property names, the same nesting, the same nulls — but describe a show that does not exist, so no
/// third party's data is vendored into the repository. Small on purpose: every entry is here because a
/// test asserts on it.
/// </summary>
internal static class TvMazeFixtures
{
    public const string ShowId = "1";

    /// <summary>
    /// <c>shows/1?embed[]=episodes&amp;embed[]=seasons</c>. Covers the cases the adapter has to get
    /// right: an HTML summary, a season with no name or image, a typed special, the season-0 bucket,
    /// an episode with no number at all (unaddressable, so dropped), and an <c>airstamp</c> whose UTC
    /// day differs from its <c>airdate</c>.
    /// </summary>
    public const string ShowWithEmbeds = """
        {
          "id": 1,
          "name": "Fake Frontier",
          "language": "English",
          "status": "Running",
          "runtime": 42,
          "averageRuntime": 44,
          "premiered": "2024-01-08",
          "ended": null,
          "summary": "<p>A <b>synthetic</b> show used only by the tests.</p>",
          "image": {
            "medium": "https://images.example/frontier-medium.jpg",
            "original": "https://images.example/frontier.jpg"
          },
          "externals": { "tvrage": 0, "thetvdb": 900001, "imdb": "tt9000001" },
          "_embedded": {
            "seasons": [
              {
                "id": 11,
                "number": 1,
                "name": "Season One",
                "episodeOrder": 3,
                "premiereDate": "2024-01-08",
                "endDate": "2024-12-24",
                "summary": "<p>The first season.</p>",
                "image": { "original": "https://images.example/frontier-s01.jpg" }
              },
              {
                "id": 12,
                "number": 2,
                "name": "",
                "episodeOrder": null,
                "premiereDate": "2025-02-03",
                "endDate": null,
                "summary": null,
                "image": null
              }
            ],
            "episodes": [
              {
                "id": 101,
                "name": "Departure",
                "season": 1,
                "number": 1,
                "type": "regular",
                "airdate": "2024-01-08",
                "airtime": "21:00",
                "airstamp": "2024-01-08T21:00:00-05:00",
                "runtime": 42,
                "summary": "<p>They <i>leave</i> at last.</p>",
                "image": { "original": "https://images.example/frontier-s01e01.jpg" }
              },
              {
                "id": 102,
                "name": "Arrival",
                "season": 1,
                "number": 2,
                "type": "regular",
                "airdate": "2024-01-15",
                "airstamp": "2024-01-15T21:00:00-05:00",
                "runtime": 42,
                "summary": null,
                "image": null
              },
              {
                "id": 103,
                "name": "Midwinter",
                "season": 1,
                "number": 3,
                "type": "significant_special",
                "airdate": "2024-12-24",
                "airstamp": "2024-12-24T21:00:00-05:00",
                "runtime": 30,
                "summary": null,
                "image": null
              },
              {
                "id": 104,
                "name": "Prologue",
                "season": 0,
                "number": 1,
                "type": "regular",
                "airdate": "2023-12-01",
                "airstamp": "2023-12-01T21:00:00-05:00",
                "runtime": 15,
                "summary": null,
                "image": null
              },
              {
                "id": 105,
                "name": "Unnumbered",
                "season": 2,
                "number": null,
                "type": "regular",
                "airdate": null,
                "airstamp": null,
                "runtime": null,
                "summary": null,
                "image": null
              }
            ]
          }
        }
        """;

    /// <summary><c>shows/1/images</c>. A banner is present to prove it is not mapped to Logo.</summary>
    public const string Images = """
        [
          {
            "id": 1,
            "type": "poster",
            "main": true,
            "resolutions": { "original": { "url": "https://images.example/poster.jpg", "width": 680, "height": 1000 } }
          },
          {
            "id": 2,
            "type": "background",
            "main": false,
            "resolutions": { "original": { "url": "https://images.example/background.jpg", "width": 1920, "height": 1080 } }
          },
          {
            "id": 3,
            "type": "banner",
            "main": false,
            "resolutions": { "original": { "url": "https://images.example/banner.jpg", "width": 758, "height": 140 } }
          },
          {
            "id": 4,
            "type": "typography",
            "main": false,
            "resolutions": { "original": { "url": "https://images.example/logo.png", "width": 800, "height": 310 } }
          }
        ]
        """;

    /// <summary><c>search/shows?q=…</c> — a search hit carries the same externals block as the show.</summary>
    public const string Search = """
        [
          {
            "score": 0.91,
            "show": {
              "id": 1,
              "name": "Fake Frontier",
              "premiered": "2024-01-08",
              "summary": "<p>A synthetic show.</p>",
              "externals": { "tvrage": 0, "thetvdb": 900001, "imdb": "tt9000001" }
            }
          }
        ]
        """;
}
