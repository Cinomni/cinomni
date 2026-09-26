using System.Globalization;

namespace Cinomni.Metadata.Tests.Fixtures;

/// <summary>
/// Hand-written, deliberately synthetic TheTVDB v4 payloads — real property names and nesting, an
/// invented series. Two things only this provider publishes are exercised here: <c>absoluteNumber</c>
/// (which anime resolution depends on) and several season orderings in one record.
/// </summary>
internal static class TvdbFixtures
{
    public const string SeriesId = "2";

    /// <summary><c>login</c> — the bearer exchange the token provider performs before anything else.</summary>
    public const string Login = """{ "status": "success", "data": { "token": "fake-token" } }""";

    /// <summary>
    /// <c>series/2/extended</c> with the provider's status spelling as a parameter, so the status map can
    /// be driven as a theory. <c>characters</c> is present to prove the stored raw response drops it.
    /// </summary>
    public static string SeriesExtended(string status = "Continuing") => $$"""
        {
          "status": "success",
          "data": {
            "id": 2,
            "name": "Fake Frontier",
            "overview": "A synthetic series.",
            "year": "2024",
            "firstAired": "2024-01-08",
            "lastAired": "2025-02-03",
            "originalLanguage": "eng",
            "averageRuntime": 44,
            "status": { "id": 1, "name": "{{status}}", "recordType": "series", "keepUpdated": true },
            "artworks": [
              { "id": 9001, "image": "https://artworks.example/poster.jpg", "type": 2, "language": "eng", "width": 680, "height": 1000, "score": 100 },
              { "id": 9002, "image": "https://artworks.example/background.jpg", "type": 3, "language": null, "width": 1920, "height": 1080, "score": 50 }
            ],
            "seasons": [
              { "id": 301, "seriesId": 2, "type": { "id": 1, "name": "Aired Order", "type": "official" }, "number": 1, "name": "Season One", "image": "https://artworks.example/official-s01.jpg" },
              { "id": 302, "seriesId": 2, "type": { "id": 1, "name": "Aired Order", "type": "official" }, "number": 2, "image": null },
              { "id": 303, "seriesId": 2, "type": { "id": 2, "name": "DVD Order", "type": "dvd" }, "number": 1, "image": "https://artworks.example/dvd-s01.jpg" }
            ],
            "remoteIds": [
              { "id": "tt9000001", "type": 2, "sourceName": "IMDB" },
              { "id": "777", "type": 12, "sourceName": "TheMovieDB.com" }
            ],
            "characters": [
              { "id": 1, "name": "A cast list the ACL has no use for", "peopleId": 5 },
              { "id": 2, "name": "Another one", "peopleId": 6 }
            ]
          }
        }
        """;

    /// <summary>
    /// First page of <c>series/2/episodes/{type}</c>. <c>links.next</c> is set, which is the adapter's
    /// only signal that another page exists.
    /// </summary>
    public const string EpisodesPage0 = """
        {
          "status": "success",
          "data": {
            "series": { "id": 2 },
            "episodes": [
              { "id": 401, "seriesId": 2, "name": "Departure", "aired": "2024-01-08", "runtime": 42, "overview": "They leave at last.", "image": "https://artworks.example/s01e01.jpg", "number": 1, "absoluteNumber": 1, "seasonNumber": 1 },
              { "id": 402, "seriesId": 2, "name": "Arrival", "aired": "2024-01-15", "runtime": 42, "overview": null, "image": null, "number": 2, "absoluteNumber": 2, "seasonNumber": 1 }
            ]
          },
          "links": {
            "prev": null,
            "self": "https://api4.thetvdb.example/v4/series/2/episodes/official?page=0",
            "next": "https://api4.thetvdb.example/v4/series/2/episodes/official?page=1",
            "total_items": 4,
            "page_size": 2
          }
        }
        """;

    /// <summary>Last page: <c>links.next</c> is null, so the walk stops. Season 0 has no absolute number.</summary>
    public const string EpisodesPage1 = """
        {
          "status": "success",
          "data": {
            "series": { "id": 2 },
            "episodes": [
              { "id": 403, "seriesId": 2, "name": "Interlude", "aired": "2025-02-03", "runtime": 44, "overview": null, "image": null, "number": 1, "absoluteNumber": 3, "seasonNumber": 2 },
              { "id": 404, "seriesId": 2, "name": "Prologue", "aired": "2023-12-01", "runtime": 15, "overview": null, "image": null, "number": 1, "absoluteNumber": null, "seasonNumber": 0 }
            ]
          },
          "links": {
            "prev": "https://api4.thetvdb.example/v4/series/2/episodes/official?page=0",
            "self": "https://api4.thetvdb.example/v4/series/2/episodes/official?page=1",
            "next": null,
            "total_items": 4,
            "page_size": 2
          }
        }
        """;

    /// <summary>The DVD ordering carries different numbers for the same broadcasts — the point of D-order.</summary>
    public const string DvdEpisodesPage0 = """
        {
          "status": "success",
          "data": {
            "series": { "id": 2 },
            "episodes": [
              { "id": 401, "seriesId": 2, "name": "Departure", "aired": "2024-01-08", "runtime": 42, "number": 2, "absoluteNumber": 1, "seasonNumber": 1 },
              { "id": 402, "seriesId": 2, "name": "Arrival", "aired": "2024-01-15", "runtime": 42, "number": 1, "absoluteNumber": 2, "seasonNumber": 1 }
            ]
          },
          "links": { "prev": null, "self": "…", "next": null, "total_items": 2, "page_size": 2 }
        }
        """;

    /// <summary><c>search?query=…&amp;type=series</c>: a hit publishes its cross-references as an array.</summary>
    public const string Search = """
        {
          "status": "success",
          "data": [
            {
              "tvdb_id": "2",
              "name": "Fake Frontier",
              "year": "2024",
              "overview": "A synthetic series.",
              "remote_ids": [
                { "id": "tt9000001", "type": 2, "sourceName": "IMDB" },
                { "id": "777", "type": 12, "sourceName": "TheMovieDB.com" }
              ]
            }
          ]
        }
        """;

    /// <summary>An episodes page carrying <paramref name="count"/> synthetic episodes and a next link.</summary>
    public static string EndlessEpisodesPage(int count) =>
        $$"""
        {
          "status": "success",
          "data": { "episodes": [ {{string.Join(",", Enumerable.Range(1, count).Select(Episode))}} ] },
          "links": { "next": "https://api4.thetvdb.example/v4/next" }
        }
        """;

    private static string Episode(int number) =>
        $$"""{ "id": {{number.ToString(CultureInfo.InvariantCulture)}}, "name": "E{{number.ToString(CultureInfo.InvariantCulture)}}", "number": {{number.ToString(CultureInfo.InvariantCulture)}}, "seasonNumber": 1 }""";
}
