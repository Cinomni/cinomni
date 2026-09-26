using Cinomni.Operations.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Pure unit tests for the settings store's read path: no database, deterministic inputs. Proves the
/// precedence resolver's exact provider-origin detection, the cache's generation-based caching in
/// <see cref="LiveOptions{TOptions}"/>, and <see cref="SettingsView"/>'s typed accessors.
/// </summary>
public sealed class SettingsReadPathUnitTests
{
    private static readonly SettingDefinition TextDefinition = new(
        Key: "test.text", Kind: SettingKind.Text, IsSecret: false, ConfigurationPath: "Test:Text");

    [Fact]
    public void Cache_starts_empty_at_generation_zero()
    {
        var cache = new SettingsCache();

        Assert.Equal(0, cache.Current.Generation);
        Assert.Empty(cache.Current.Values);
    }

    [Fact]
    public void Publish_replaces_the_snapshot_atomically()
    {
        var cache = new SettingsCache();
        var snapshot = new SettingsSnapshot(1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("k", "v"));

        cache.Publish(snapshot);

        Assert.Same(snapshot, cache.Current);
    }

    [Fact]
    public void LiveOptions_returns_the_cached_instance_when_the_generation_has_not_changed()
    {
        var cache = new SettingsCache();
        var builds = 0;
        var live = BuildLiveOptions(cache, _ =>
        {
            builds++;
            return new Marker();
        });

        var first = live.Current;
        var second = live.Current;

        Assert.Same(first, second);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void LiveOptions_rebuilds_when_the_generation_changes()
    {
        var cache = new SettingsCache();
        var builds = 0;
        var live = BuildLiveOptions(cache, _ =>
        {
            builds++;
            return new Marker();
        });

        var first = live.Current;
        cache.Publish(new SettingsSnapshot(1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty));
        var second = live.Current;

        Assert.NotSame(first, second);
        Assert.Equal(2, builds);
    }

    /// <summary>
    /// Builds an <see cref="ILiveOptions{TOptions}"/> through the real registration extension and DI
    /// container — the same path a module uses — rather than reaching into the internal implementation.
    /// </summary>
    private static ILiveOptions<Marker> BuildLiveOptions(SettingsCache cache, Func<SettingsView, Marker> binder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(cache);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLiveOptions(binder);

        return services.BuildServiceProvider().GetRequiredService<ILiveOptions<Marker>>();
    }

    [Fact]
    public void Precedence_treats_a_command_line_value_as_pinned()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Test:Text"] = "from-file" })
            .AddCommandLine(["--Test:Text=from-cli"])
            .Build();

        Assert.True(SettingsPrecedence.IsPinned(configuration, "Test:Text"));
        Assert.Equal("from-cli", configuration["Test:Text"]);
    }

    [Fact]
    public void Precedence_treats_an_environment_variable_as_pinned()
    {
        using var probe = new TemporaryEnvironmentVariable("Test__Text", "from-env");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Test:Text"] = "from-file" })
            .AddEnvironmentVariables()
            .Build();

        Assert.True(SettingsPrecedence.IsPinned(configuration, "Test:Text"));
    }

    [Fact]
    public void Precedence_does_not_treat_an_empty_environment_variable_as_pinned()
    {
        // A compose file forwards `${TMDB_API_KEY:-}`, so an unset key reaches the process as an empty
        // variable. Pinning on it would hide every stored value behind a value nobody gave.
        using var probe = new TemporaryEnvironmentVariable("Test__Text", " ");

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        Assert.False(SettingsPrecedence.IsPinned(configuration, "Test:Text"));
    }

    [Fact]
    public void Precedence_does_not_treat_a_file_or_in_memory_value_as_pinned()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Test:Text"] = "from-file" })
            .Build();

        Assert.False(SettingsPrecedence.IsPinned(configuration, "Test:Text"));
    }

    [Fact]
    public void Precedence_reports_false_for_an_absent_key()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.False(SettingsPrecedence.IsPinned(configuration, "Test:Missing"));
    }

    [Fact]
    public void View_prefers_the_database_override_over_the_configuration_file_when_not_pinned()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Test:Text"] = "from-file" })
            .Build();
        var snapshot = new SettingsSnapshot(
            1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("test.text", "from-database"));

        var view = new SettingsView(snapshot, configuration);

        Assert.Equal("from-database", view.GetString(TextDefinition));
    }

    [Fact]
    public void View_ignores_a_stored_row_when_the_environment_pins_the_same_path()
    {
        using var probe = new TemporaryEnvironmentVariable("Test__Text", "from-env");

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var snapshot = new SettingsSnapshot(
            1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("test.text", "from-database"));

        var view = new SettingsView(snapshot, configuration);

        Assert.Equal("from-env", view.GetString(TextDefinition));
    }

    [Fact]
    public void View_falls_back_to_the_definitions_default_when_nothing_else_supplies_a_value()
    {
        var configuration = new ConfigurationBuilder().Build();
        var definition = TextDefinition with { DefaultAsString = "code-default" };

        var view = new SettingsView(SettingsSnapshot.Empty, configuration);

        Assert.Equal("code-default", view.GetString(definition));
    }

    [Fact]
    public void View_parses_typed_values()
    {
        var configuration = new ConfigurationBuilder().Build();
        var snapshot = new SettingsSnapshot(1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
            .Add("test.bool", "true")
            .Add("test.int", "42")
            .Add("test.duration", "00:00:30"));
        var view = new SettingsView(snapshot, configuration);

        var boolDefinition = TextDefinition with { Key = "test.bool" };
        var intDefinition = TextDefinition with { Key = "test.int" };
        var durationDefinition = TextDefinition with { Key = "test.duration" };

        Assert.True(view.GetBool(boolDefinition));
        Assert.Equal(42, view.GetInt(intDefinition));
        Assert.Equal(TimeSpan.FromSeconds(30), view.GetTimeSpan(durationDefinition));
    }

    [Fact]
    public void View_reads_a_stored_list_as_a_comma_joined_string()
    {
        var configuration = new ConfigurationBuilder().Build();
        var snapshot = new SettingsSnapshot(
            1, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("test.list", "en, es ,fr"));
        var view = new SettingsView(snapshot, configuration);

        var listDefinition = TextDefinition with { Key = "test.list", Kind = SettingKind.List };

        Assert.Equal(["en", "es", "fr"], view.GetStringArray(listDefinition));
    }

    [Fact]
    public void View_reads_a_json_array_section_when_no_database_override_exists()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Test:List:0"] = "en",
                ["Test:List:1"] = "es",
            })
            .Build();
        var view = new SettingsView(SettingsSnapshot.Empty, configuration);

        var listDefinition = TextDefinition with { Key = "test.list", Kind = SettingKind.List, ConfigurationPath = "Test:List" };

        Assert.Equal(["en", "es"], view.GetStringArray(listDefinition));
    }

    private sealed class Marker;

    /// <summary>Sets a process environment variable for the lifetime of the instance, then clears it.</summary>
    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string _name;

        public TemporaryEnvironmentVariable(string name, string value)
        {
            _name = name;
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, null);
    }
}
