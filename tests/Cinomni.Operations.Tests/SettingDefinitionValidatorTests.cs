using Cinomni.Operations.Settings;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Gate (a) of the write-path validation pipeline: purely data-driven, so every case here proves the
/// generic kind/range/regex/enum machinery works for values no <see cref="SettingDefinition"/> has been
/// registered for yet — the catalogue itself stays empty until a later increment wires a real key.
/// </summary>
public sealed class SettingDefinitionValidatorTests
{
    [Fact]
    public void Clearing_a_key_needs_no_validation_at_all()
    {
        var definition = Number("retention.operations.batchSize", min: 1, max: 100_000);
        var change = SettingChange.Clear(definition.Key, expectedVersion: 3);

        Assert.Empty(SettingDefinitionValidator.Validate(definition, change));
    }

    [Fact]
    public void An_empty_non_required_value_is_accepted_as_the_documented_disabled_state()
    {
        var definition = Secret("metadata.tmdb.apiKey", minLength: 8, maxLength: 200);

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "", 0));

        Assert.Empty(errors);
    }

    [Fact]
    public void A_required_value_left_empty_is_rejected()
    {
        var definition = new SettingDefinition(
            "subtitles.wantedLanguages", SettingKind.List, false, "Subtitles:WantedLanguages",
            Validation: new SettingValidation(Required: true));

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "", 0)).ToList();

        var error = Assert.Single(errors);
        Assert.Equal("settings.value_required", error.Code);
        Assert.Equal([definition.Key], error.Keys);
    }

    [Theory]
    [InlineData("short")] // below MinLength
    [InlineData("a-value-that-is-far-too-long-for-an-api-key-field-and-must-be-rejected-outright-here")]
    public void A_secret_value_outside_its_length_bounds_is_rejected_without_echoing_it(string value)
    {
        var definition = Secret("metadata.tmdb.apiKey", minLength: 8, maxLength: 40);

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, value, 0)).ToList();

        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
            Assert.Contains("value withheld", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_non_secret_rejection_names_the_offending_value()
    {
        var definition = Text("metadata.language", minLength: 2, maxLength: 15, pattern: "^[A-Za-z-]+$");

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "en US", 0)).ToList();

        var error = Assert.Single(errors);
        Assert.Contains("en US", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")] // below MinValue
    [InlineData("100001")] // above MaxValue
    public void A_number_outside_its_shape_or_range_is_rejected(string value)
    {
        var definition = Number("retention.operations.batchSize", min: 1, max: 100_000);

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, value, 0));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_number_within_range_is_accepted()
    {
        var definition = Number("retention.operations.batchSize", min: 1, max: 100_000);

        Assert.Empty(SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "5000", 0)));
    }

    /// <summary>
    /// A zero-padded numeric literal parses to an in-range value, so the length bound is the only gate
    /// that can reject it before it ever reaches an audit column sized for an ordinary value.
    /// </summary>
    [Fact]
    public void A_number_value_far_longer_than_its_MaxLength_is_rejected()
    {
        var definition = new SettingDefinition(
            "retention.operations.batchSize", SettingKind.Number, false, "Retention:BatchSize",
            Validation: new SettingValidation(MinValue: 1, MaxValue: 100_000, MaxLength: 12));
        var padded = new string('0', 500) + "5000";

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, padded, 0)).ToList();

        Assert.Contains(errors, e => e.Code == "settings.too_long");
    }

    /// <summary>
    /// A number-kind Pattern narrows the accepted format beyond "any double" — e.g. digits only — so a
    /// value like "7.5" that a fractional binder would read fine, but an integer one would silently drop
    /// to its fallback default, is rejected here instead.
    /// </summary>
    [Fact]
    public void A_number_value_not_matching_its_Pattern_is_rejected()
    {
        var definition = new SettingDefinition(
            "retention.operations.batchSize", SettingKind.Number, false, "Retention:BatchSize",
            Validation: new SettingValidation(MinValue: 1, MaxValue: 100_000, Pattern: "^[0-9]+$"));

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "7.5", 0)).ToList();

        Assert.Contains(errors, e => e.Code == "settings.pattern_mismatch");
    }

    [Theory]
    [InlineData("not-a-bool")]
    [InlineData("2")]
    public void A_non_boolean_value_is_rejected(string value)
    {
        var definition = new SettingDefinition(
            "subtitles.hearingImpaired", SettingKind.Boolean, false, "Subtitles:HearingImpaired");

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, value, 0));

        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("not-a-duration")]
    [InlineData("-00:00:01")] // below MinValue (zero)
    [InlineData("00:06:00")] // above the 5-minute MaxValue
    public void A_duration_outside_its_shape_or_range_is_rejected(string value)
    {
        var definition = new SettingDefinition(
            "subtitles.providerCallInterval", SettingKind.Duration, false, "Subtitles:ProviderCallInterval",
            Validation: new SettingValidation(MinValue: 0, MaxValue: TimeSpan.FromMinutes(5).TotalSeconds));

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, value, 0));

        Assert.NotEmpty(errors);
    }

    /// <summary>
    /// A duration close to <see cref="TimeSpan.MaxValue"/> is a legal <c>TimeSpan.TryParse</c> result but
    /// throws in ordinary arithmetic against <see cref="DateTimeOffset.UtcNow"/> — exactly the arithmetic
    /// every purge handler and the backup schedule perform. The range ceiling is what keeps it out.
    /// </summary>
    [Fact]
    public void A_duration_near_TimeSpan_MaxValue_is_rejected_by_the_range_ceiling()
    {
        var definition = new SettingDefinition(
            "retention.operations.failedCommandRetention", SettingKind.Duration, false,
            "Retention:FailedCommandRetention", Validation: SettingValidationPresets.Duration);

        var errors = SettingDefinitionValidator.Validate(
            definition, SettingChange.Set(definition.Key, "10675199.02:48:05", 0)).ToList();

        var error = Assert.Single(errors);
        Assert.Equal("settings.above_maximum", error.Code);
    }

    /// <summary>A zero-padded duration literal must not reach the audit table's bounded column either.</summary>
    [Fact]
    public void A_duration_value_far_longer_than_its_MaxLength_is_rejected()
    {
        var definition = new SettingDefinition(
            "subtitles.providerCallInterval", SettingKind.Duration, false, "Subtitles:ProviderCallInterval",
            Validation: new SettingValidation(MinValue: 0, MaxValue: TimeSpan.FromDays(1).TotalSeconds, MaxLength: 16));
        var padded = new string('0', 500) + "7.00:00:00";

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, padded, 0)).ToList();

        Assert.Contains(errors, e => e.Code == "settings.too_long");
    }

    [Fact]
    public void An_enum_value_outside_the_closed_set_is_rejected()
    {
        var definition = SeasonType();

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "alternate", 0)).ToList();

        var error = Assert.Single(errors);
        Assert.Equal("settings.not_a_member", error.Code);
    }

    [Fact]
    public void An_enum_value_in_the_closed_set_is_accepted()
    {
        var definition = SeasonType();

        Assert.Empty(SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "dvd", 0)));
    }

    [Fact]
    public void An_empty_list_is_accepted_when_the_key_does_not_require_a_value()
    {
        var definition = List("subtitles.wantedLanguages", minEntries: 1, maxEntries: 10, pattern: "^[a-z]{2,3}$");

        // An empty string is the "disabled" degradation for most kinds; only Required (a separate flag,
        // not set here) turns it into a rejection — see A_required_value_left_empty_is_rejected.
        Assert.Empty(SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "", 0)));
    }

    [Fact]
    public void A_list_with_a_duplicate_entry_is_rejected()
    {
        var definition = List("subtitles.wantedLanguages", minEntries: 1, maxEntries: 10, pattern: "^[a-z]{2,3}$");

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "en,fr,en", 0)).ToList();

        Assert.Contains(errors, e => e.Code == "settings.duplicate_entry");
    }

    [Fact]
    public void A_list_with_too_many_entries_is_rejected()
    {
        var definition = List("subtitles.wantedLanguages", minEntries: 1, maxEntries: 2, pattern: "^[a-z]{2,3}$");

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "en,fr,de", 0)).ToList();

        Assert.Contains(errors, e => e.Code == "settings.too_many_entries");
    }

    [Fact]
    public void A_list_entry_outside_the_closed_set_is_rejected()
    {
        var definition = new SettingDefinition(
            "metadata.providers", SettingKind.List, false, "Metadata:Providers",
            Validation: new SettingValidation(MinLength: 1, MaxLength: 3, AllowedValues: ["tmdb", "tvdb", "tvmaze"]));

        var errors = SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "tmdb,imdb", 0)).ToList();

        Assert.Contains(errors, e => e.Code == "settings.not_a_member");
    }

    [Fact]
    public void A_well_formed_list_is_accepted()
    {
        var definition = List("subtitles.wantedLanguages", minEntries: 1, maxEntries: 10, pattern: "^[a-z]{2,3}$");

        Assert.Empty(SettingDefinitionValidator.Validate(definition, SettingChange.Set(definition.Key, "en, fr", 0)));
    }

    private static SettingDefinition Text(string key, int minLength, int maxLength, string pattern) =>
        new(key, SettingKind.Text, false, key,
            Validation: new SettingValidation(MinLength: minLength, MaxLength: maxLength, Pattern: pattern));

    private static SettingDefinition Secret(string key, int minLength, int maxLength) =>
        new(key, SettingKind.Secret, true, key,
            Validation: new SettingValidation(MinLength: minLength, MaxLength: maxLength));

    private static SettingDefinition Number(string key, double min, double max) =>
        new(key, SettingKind.Number, false, key, Validation: new SettingValidation(MinValue: min, MaxValue: max));

    private static SettingDefinition List(string key, int minEntries, int maxEntries, string pattern) =>
        new(key, SettingKind.List, false, key,
            Validation: new SettingValidation(MinLength: minEntries, MaxLength: maxEntries, Pattern: pattern));

    private static SettingDefinition SeasonType() =>
        new("metadata.tvdb.seasonType", SettingKind.Enum, false, "Metadata:Tvdb:SeasonType",
            Validation: new SettingValidation(AllowedValues: ["official", "dvd", "absolute"]));
}
