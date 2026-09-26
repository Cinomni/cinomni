using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Operations.Backup;

/// <summary>
/// One schema the backup covers, and how to read its migration state.
/// <para>
/// The platform never names a module type: the composition root registers one of these per context, so
/// the list of covered schemas is a wiring fact the Host owns and a test can enumerate. Both callbacks
/// receive a <b>scoped</b> service provider, because a <c>DbContext</c> is scoped.
/// </para>
/// </summary>
/// <param name="Schema">The PostgreSQL schema the context owns.</param>
/// <param name="ReadAppliedMigrationsAsync">Reads the applied migrations this context can see.</param>
/// <param name="ReadKnownMigrations">Reads the migrations compiled into this build, without touching the database.</param>
public sealed record SchemaVersionSource(
    string Schema,
    Func<IServiceProvider, CancellationToken, Task<IReadOnlyList<string>>> ReadAppliedMigrationsAsync,
    Func<IServiceProvider, IReadOnlyList<string>> ReadKnownMigrations);

/// <summary>
/// What a manifest records about the installation's migration state.
/// </summary>
/// <param name="Schemas">Per-schema, the migrations of that module the database had applied.</param>
/// <param name="UnrecognizedMigrations">
/// Applied migrations no registered context claims. Empty on a healthy installation; anything in it came
/// from a build that had a module, or a migration, this one does not — which is exactly what a restore
/// must refuse.
/// </param>
public sealed record BackupInventory(
    IReadOnlyList<BackupSchemaState> Schemas,
    IReadOnlyList<string> UnrecognizedMigrations);

/// <summary>
/// Fans out over every registered <see cref="SchemaVersionSource"/> to produce the schema state a
/// manifest records, and the compiled state a restore is checked against.
/// <para>
/// <b>The migration history is not per schema.</b> No context configures a migrations-history table, so
/// every one of them records into the single <c>public.__EFMigrationsHistory</c> that the first migration
/// to run creates — asking any context for its applied migrations returns the whole installation's list,
/// not that module's. The inventory therefore attributes each applied id to the context that carries it,
/// and reports the ones nobody carries separately. Reading a context's list as if it were its own would
/// make every manifest claim sixteen copies of the same thing and make every restore refuse itself.
/// </para>
/// <para>
/// The union is taken across all sources rather than read once, so the same code stays correct if a
/// context is ever given a history table of its own.
/// </para>
/// </summary>
public sealed class SchemaInventory(IServiceProvider services, IEnumerable<SchemaVersionSource> sources)
{
    private readonly IReadOnlyList<SchemaVersionSource> _sources = [.. sources];

    /// <summary>Every schema the backup covers, in registration order (the Host registers them in migrate order).</summary>
    public IReadOnlyList<string> Schemas => [.. _sources.Select(source => source.Schema)];

    /// <summary>Reads the installation's migration state. One round trip per schema.</summary>
    public async Task<BackupInventory> ReadAsync(CancellationToken cancellationToken = default)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var known = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var source in _sources)
        {
            applied.UnionWith(await source.ReadAppliedMigrationsAsync(services, cancellationToken));
            known[source.Schema] = source.ReadKnownMigrations(services);
        }

        // Each context's own migrations, in the order that context declares them, filtered to the ones
        // the database had. Ordering by the compiled list rather than by the history table keeps the
        // manifest readable and stable across restores.
        var states = _sources
            .Select(source => new BackupSchemaState(
                source.Schema,
                [.. known[source.Schema].Where(applied.Contains)]))
            .ToArray();

        var claimed = known.Values.SelectMany(migrations => migrations).ToHashSet(StringComparer.Ordinal);
        var unrecognized = applied.Where(migration => !claimed.Contains(migration))
            .OrderBy(migration => migration, StringComparer.Ordinal)
            .ToArray();

        return new BackupInventory(states, unrecognized);
    }

    /// <summary>The migrations this build carries, by schema. Pure: no database is touched.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadKnown() =>
        _sources.ToDictionary(
            source => source.Schema,
            source => source.ReadKnownMigrations(services),
            StringComparer.Ordinal);
}

/// <summary>Registers the schema sources a backup manifest is built from.</summary>
public static class SchemaInventoryRegistration
{
    /// <summary>
    /// Declares that <typeparamref name="TContext"/> owns <paramref name="schema"/> and that its
    /// migration state belongs in every manifest. Registering a context here is what puts its schema
    /// inside the compatibility gate; a module whose context is missing would be dumped by
    /// <c>pg_dump</c> (which takes the whole database) and then reported as an unrecognized migration
    /// on every restore, because nothing in this build claims the ids it applied.
    /// </summary>
    public static IServiceCollection AddBackupSchema<TContext>(this IServiceCollection services, string schema)
        where TContext : Microsoft.EntityFrameworkCore.DbContext =>
        services.AddSingleton(new SchemaVersionSource(
            schema,
            static async (provider, cancellationToken) =>
            {
                var context = provider.GetRequiredService<TContext>();
                var applied = await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions
                    .GetAppliedMigrationsAsync(context.Database, cancellationToken);
                return [.. applied];
            },
            static provider =>
            {
                var context = provider.GetRequiredService<TContext>();
                return [.. Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetMigrations(context.Database)];
            }));
}
