using Cinomni.Kernel.Results;

namespace Cinomni.ReleaseParsing.Contracts;

/// <summary>
/// Public parsing surface of the Release Parsing module: turn a raw release name into a
/// structured <see cref="ParsedRelease"/>. Pure and deterministic — no I/O, no side effects.
/// An unparseable title is an explicit failure (<c>parsing.unable_to_parse</c>), never an
/// exception. Decision calls this over each raw title from a search.
/// </summary>
public interface IReleaseParser
{
    Result<ParsedRelease> Parse(string releaseTitle);
}

/// <summary>
/// The numbering-only slice of the parser, for callers that hold a name but not a release: Import
/// resolves a media <b>file name</b> to its season/episode through this interface instead of
/// duplicating the regexes, so there is exactly one numbering vocabulary in the platform.
/// </summary>
/// <remarks>
/// Usable standalone on a bare file name — no year, no scene tags, no release group required — and
/// it never throws: an unrecognised name (or a pathological one that trips the regex timeout)
/// simply yields <see langword="null"/>.
/// </remarks>
public interface IEpisodeNumberParser
{
    /// <summary>Extracts the numbering from <paramref name="name"/>, or <see langword="null"/> when it carries none.</summary>
    EpisodeNumbering? Parse(string name);
}
