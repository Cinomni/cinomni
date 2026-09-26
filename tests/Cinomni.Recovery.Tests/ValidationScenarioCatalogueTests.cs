using System.Reflection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Keeps the validation-scenario catalogue honest. It asserts nothing about media, downloads or
/// imports — it asserts that the list itself cannot rot.
/// <para>
/// Three ways a catalogue stops being true, each with a test: it stops covering all fifteen, it
/// names a suite that no longer exists, or the prose a human reads drifts from the data a machine
/// reads. Before this file the fifteen scenarios lived only outside the repository, so none of the
/// three was detectable at all.
/// </para>
/// </summary>
public sealed class ValidationScenarioCatalogueTests
{
    /// <summary>The <c>tests/</c> directory, found by walking up from the test binary.</summary>
    private static readonly DirectoryInfo TestsRoot = FindTestsRoot();

    [Fact]
    public void The_catalogue_holds_exactly_the_fifteen_scenarios_numbered_one_to_fifteen()
    {
        var scenarios = ValidationScenarios.All;

        Assert.Equal(ValidationScenarios.ExpectedCount, scenarios.Count);
        Assert.Equal(
            Enumerable.Range(1, ValidationScenarios.ExpectedCount),
            scenarios.Select(s => s.Number).Order());
        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_scenario_states_an_outcome_and_names_at_least_one_test()
    {
        foreach (var scenario in ValidationScenarios.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Name), $"Scenario {scenario.Number} has no name.");
            Assert.False(
                string.IsNullOrWhiteSpace(scenario.Statement),
                $"Scenario {scenario.Number} states nothing that could be checked.");
            Assert.NotEmpty(scenario.VerifiedBy);
        }
    }

    [Fact]
    public void Every_test_the_catalogue_cites_still_exists()
    {
        // A renamed or deleted suite is the ordinary way coverage disappears without anyone deciding
        // to drop it. Resolving the path is what turns that into a build failure.
        foreach (var scenario in ValidationScenarios.All)
        {
            foreach (var relativePath in scenario.VerifiedBy)
            {
                var path = Path.Combine(TestsRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(
                    File.Exists(path),
                    $"Scenario {scenario.Number} cites {relativePath}, which does not exist under tests/.");
            }
        }
    }

    [Fact]
    public void The_scenarios_this_suite_owns_are_carried_by_a_class_that_claims_them()
    {
        // The trait is what a `dotnet test --filter "Scenario=14"` run selects, so a class that owns a
        // scenario and forgets to say so is unreachable by the one query an operator would try.
        var claimed = ScenarioClaims().Select(claim => claim.Number).ToHashSet(StringComparer.Ordinal);

        var owned = ValidationScenarios.All
            .Where(s => s.VerifiedBy.Any(path => path.StartsWith(OwnSuitePrefix, StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(owned);
        foreach (var scenario in owned)
        {
            Assert.Contains($"{scenario.Number:D2}", claimed);
        }
    }

    [Fact]
    public void Every_trait_this_suite_carries_names_a_scenario_that_exists()
    {
        var numbers = ValidationScenarios.All.Select(s => $"{s.Number:D2}").ToHashSet(StringComparer.Ordinal);

        foreach (var claim in ScenarioClaims())
        {
            Assert.True(
                numbers.Contains(claim.Number),
                $"{claim.ClassName} claims scenario {claim.Number}, which is not in the catalogue.");
        }
    }

    [Fact]
    public void The_prose_catalogue_and_the_data_catalogue_agree()
    {
        // tests/VALIDATION-SCENARIOS.md is what a contributor reads. Editing one and not the other is
        // how a catalogue starts lying, so the headings are parsed and matched exactly.
        var document = File.ReadAllLines(Path.Combine(TestsRoot.FullName, "VALIDATION-SCENARIOS.md"));

        var headings = document
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .Where(heading => heading.Length > 0 && char.IsAsciiDigit(heading[0]))
            .ToList();

        Assert.Equal(
            ValidationScenarios.All.OrderBy(s => s.Number).Select(s => $"{s.Number}. {s.Name}"),
            headings);
    }

    /// <summary>The trait name a scenario class uses to claim its number.</summary>
    private const string ScenarioTrait = "Scenario";

    private const string OwnSuitePrefix = "Cinomni.Recovery.Tests/";

    /// <summary>One class in this assembly claiming one scenario number through its trait.</summary>
    private readonly record struct ScenarioClaim(string ClassName, string Number);

    /// <summary>
    /// Every <c>[Trait("Scenario", "NN")]</c> in this assembly. Read through
    /// <see cref="CustomAttributeData"/> rather than the attribute instance, because xUnit's
    /// <see cref="TraitAttribute"/> keeps its name and value as constructor arguments and exposes
    /// neither as a property.
    /// </summary>
    private static IEnumerable<ScenarioClaim> ScenarioClaims() =>
        Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(type => type.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(TraitAttribute)
                    && attribute.ConstructorArguments.Count == 2
                    && (string?)attribute.ConstructorArguments[0].Value == ScenarioTrait)
                .Select(attribute => new ScenarioClaim(
                    type.Name,
                    (string?)attribute.ConstructorArguments[1].Value ?? string.Empty)));

    /// <summary>
    /// Walks up from the test binary to the <c>tests/</c> directory. The catalogue is a repository
    /// file rather than an embedded resource on purpose: a contributor has to be able to read and
    /// edit it without building anything.
    /// </summary>
    private static DirectoryInfo FindTestsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VALIDATION-SCENARIOS.md")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate tests/VALIDATION-SCENARIOS.md above the test binary.");
    }
}
