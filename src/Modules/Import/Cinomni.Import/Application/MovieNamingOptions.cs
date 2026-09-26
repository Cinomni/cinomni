using Cinomni.Operations.Settings;

namespace Cinomni.Import.Application;

/// <summary>
/// How a movie file is named when it lands. Closed on purpose: a free-form token string is a second
/// language the household has to learn, and a bad token writes a path nothing can explain later.
/// </summary>
public enum MovieNamingFormat
{
    /// <summary>The release file name, which is what every installation has written until now.</summary>
    ReleaseName,

    /// <summary>The catalog title and year, as <c>Title (Year)/Title (Year).ext</c>.</summary>
    TitleYear,
}

/// <summary>The one settable choice for movie library names. Series already land under the catalog title.</summary>
public sealed class MovieNamingOptions
{
    internal const string FormatKey = "import.movieNaming";

    public MovieNamingFormat Format { get; init; } = MovieNamingFormat.ReleaseName;

    public static IEnumerable<SettingError> Check(MovieNamingOptions candidate)
    {
        if (!Enum.IsDefined(candidate.Format))
        {
            yield return new SettingError(
                [FormatKey],
                "settings.invalid_value",
                $"{nameof(Format)} is not a known movie naming format.");
        }
    }
}

/// <summary>The one settable key backed by <see cref="MovieNamingOptions"/>.</summary>
public static class MovieNamingSettingDefinitions
{
    public static readonly string[] Allowed = ["ReleaseName", "TitleYear"];

    public static readonly SettingDefinition Format = new(
        MovieNamingOptions.FormatKey,
        SettingKind.Enum,
        IsSecret: false,
        "Import:MovieNaming",
        nameof(MovieNamingFormat.ReleaseName),
        new SettingValidation(Required: true, MaxLength: 32, AllowedValues: Allowed));
}
