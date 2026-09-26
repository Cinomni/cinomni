namespace Cinomni.Metadata.Tests.Fixtures;

/// <summary>
/// Hand-written, deliberately synthetic TMDB payloads — real property names and nesting, an invented
/// show. TMDB does not embed episodes, so the series fixtures come as one detail document plus one
/// document per season; that fan-out is exactly what the per-refresh season cap bounds.
/// </summary>
internal static class TmdbFixtures
{
    public const string SeriesId = "3";
    public const string MovieId = "603";

    /// <summary><c>tv/3?append_to_response=images,external_ids</c>.</summary>
    public const string TvDetail = """
        {
          "id": 3,
          "name": "Fake Frontier",
          "original_name": "Fake Frontier",
          "overview": "A synthetic series.",
          "first_air_date": "2024-01-08",
          "last_air_date": "2025-02-03",
          "status": "Returning Series",
          "episode_run_time": [44],
          "original_language": "en",
          "poster_path": "/poster.jpg",
          "backdrop_path": "/backdrop.jpg",
          "number_of_seasons": 2,
          "seasons": [
            { "air_date": "2024-01-08", "episode_count": 2, "id": 502, "name": "Season 1", "overview": "The first season.", "poster_path": "/s01.jpg", "season_number": 1 },
            { "air_date": "2023-12-01", "episode_count": 1, "id": 501, "name": "Specials", "overview": "", "poster_path": "/s00.jpg", "season_number": 0 },
            { "air_date": "2025-02-03", "episode_count": 1, "id": 503, "name": "Season 2", "overview": "", "poster_path": null, "season_number": 2 }
          ],
          "images": {
            "posters": [ { "file_path": "/poster-en.jpg", "iso_639_1": "en", "width": 680, "height": 1000, "vote_average": 5.4, "vote_count": 10 } ],
            "backdrops": [ { "file_path": "/backdrop-wide.jpg", "iso_639_1": null, "width": 1920, "height": 1080, "vote_average": 5.0, "vote_count": 3 } ],
            "logos": []
          },
          "external_ids": { "imdb_id": "tt9000001", "tvdb_id": 900001, "freebase_id": null }
        }
        """;

    /// <summary><c>tv/3/season/0</c> — the specials bucket.</summary>
    public const string Season0 = """
        {
          "id": 501,
          "air_date": "2023-12-01",
          "name": "Specials",
          "season_number": 0,
          "episodes": [
            { "air_date": "2023-12-01", "episode_number": 1, "id": 601, "name": "Prologue", "overview": null, "runtime": 15, "season_number": 0, "still_path": "/s00e01.jpg" }
          ]
        }
        """;

    /// <summary><c>tv/3/season/1</c>.</summary>
    public const string Season1 = """
        {
          "id": 502,
          "air_date": "2024-01-08",
          "name": "Season 1",
          "season_number": 1,
          "episodes": [
            { "air_date": "2024-01-08", "episode_number": 1, "id": 602, "name": "Departure", "overview": "They leave at last.", "runtime": 42, "season_number": 1, "still_path": "/s01e01.jpg" },
            { "air_date": "2024-01-15", "episode_number": 2, "id": 603, "name": "Arrival", "overview": null, "runtime": 42, "season_number": 1, "still_path": null }
          ]
        }
        """;

    /// <summary><c>tv/3/season/2</c> — reached only when the season cap allows a third request.</summary>
    public const string Season2 = """
        {
          "id": 503,
          "air_date": "2025-02-03",
          "name": "Season 2",
          "season_number": 2,
          "episodes": [
            { "air_date": "2025-02-03", "episode_number": 1, "id": 604, "name": "Interlude", "overview": null, "runtime": 44, "season_number": 2, "still_path": null }
          ]
        }
        """;

    /// <summary><c>search/tv?query=…</c>.</summary>
    public const string SearchTv = """
        {
          "page": 1,
          "results": [
            { "id": 3, "name": "Fake Frontier", "original_name": "Fake Frontier", "first_air_date": "2024-01-08", "overview": "A synthetic series." }
          ],
          "total_results": 1
        }
        """;

    /// <summary><c>movie/603?append_to_response=images</c> — the movie regression fixture.</summary>
    public const string MovieDetail = """
        {
          "id": 603,
          "title": "Fake Feature",
          "original_title": "Fake Feature",
          "overview": "A synthetic film.",
          "release_date": "1999-03-31",
          "runtime": 136,
          "original_language": "en",
          "poster_path": "/movie-poster.jpg",
          "backdrop_path": "/movie-backdrop.jpg",
          "images": {
            "posters": [ { "file_path": "/movie-poster-en.jpg", "iso_639_1": "en", "width": 2000, "height": 3000, "vote_average": 8.0, "vote_count": 100 } ],
            "backdrops": [],
            "logos": []
          }
        }
        """;
}
