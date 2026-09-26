using System.Globalization;
using Cinomni.Operations.Backup;
using Cinomni.Operations.Persistence;

namespace Cinomni.Host;

/// <summary>
/// The Host's <c>backup</c> verbs: <c>create</c>, <c>list</c>, <c>verify</c> and <c>check</c>.
/// <para>
/// They exist because the alternative was an HTTP route, and a route is the wrong shape for this. A dump
/// contains delivery-channel URLs, indexer addresses that commonly embed keys, Argon2id password
/// verifiers and session token hashes — one authorization slip would hand over the entire installation,
/// so there is deliberately no network path to a backup at all. The operator already has shell access to
/// the node; that is the gate.
/// </para>
/// <para>
/// <b>Restore is not here.</b> It drops and recreates every schema, so the Host must be stopped for it —
/// the outbox relay, the command worker and the scheduler would otherwise be operating on a half-restored
/// database and could publish events from it. <c>scripts/cinomni-restore.sh</c> is the restore path, and
/// <c>backup check</c> is the gate it tells the operator to run first.
/// </para>
/// <para>
/// The verbs run <b>before</b> the migrate sequence, which is load-bearing for <c>check</c>: against an
/// empty database, migrating first would create all sixteen schemas and mask the very mismatch the check
/// exists to find.
/// </para>
/// </summary>
internal sealed record BackupCommandLine(string Verb, string? Name, string[] HostArguments)
{
    private const string RootVerb = "backup";

    private static readonly string[] Verbs = ["create", "list", "verify", "check"];

    /// <summary>Exit code for a usage mistake, kept distinct from a refusal so a script can tell them apart.</summary>
    private const int UsageExitCode = 2;

    /// <summary>
    /// Recognises <c>backup &lt;verb&gt; [name]</c> and returns the remaining arguments for the Host
    /// builder, so configuration overrides on the same command line still reach it. Returns <c>null</c>
    /// when this is a normal start.
    /// </summary>
    public static BackupCommandLine? TryParse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], RootVerb, StringComparison.Ordinal))
        {
            return null;
        }

        var verb = args.Length > 1 ? args[1] : string.Empty;
        var consumed = 2;

        string? name = null;
        if (args.Length > 2 && !args[2].StartsWith('-'))
        {
            name = args[2];
            consumed = 3;
        }

        return new BackupCommandLine(verb, name, [.. args.Skip(Math.Min(consumed, args.Length))]);
    }

    /// <summary>Runs the verb. 0 on success, 1 on a refusal or failure, 2 on a usage mistake.</summary>
    public async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (!Verbs.Contains(Verb, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                $"Unknown verb '{Verb}'. Usage: {RootVerb} <{string.Join('|', Verbs)}> [name]");
            return UsageExitCode;
        }

        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<BackupStore>();

        try
        {
            return Verb switch
            {
                "create" => await CreateAsync(scope.ServiceProvider, cancellationToken),
                "list" => List(store),
                "verify" => await VerifyAsync(scope.ServiceProvider, store, cancellationToken),
                "check" => await CheckAsync(scope.ServiceProvider, store, cancellationToken),
                _ => UsageExitCode,
            };
        }
        catch (Exception exception) when (BackupStore.IsFilesystemFailure(exception) || exception is Npgsql.NpgsqlException)
        {
            // A verb is run by an operator in the middle of an upgrade or a restore. An unreadable
            // directory or a database that is not up must exit 1 with a sentence, not a stack trace —
            // the documented exit codes are what a wrapping script reads.
            Console.Error.WriteLine($"Backup {Verb} failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> CreateAsync(IServiceProvider scope, CancellationToken cancellationToken)
    {
        var service = scope.GetRequiredService<BackupService>();
        var outcome = await service.CreateAsync(BackupTrigger.Manual, cancellationToken);

        if (outcome.IsFailure)
        {
            Console.Error.WriteLine($"Backup failed [{outcome.Error.Code}]: {outcome.Error.Message}");
            return 1;
        }

        var manifest = outcome.Value;
        Console.WriteLine($"Wrote {manifest.DumpFileName} ({manifest.DumpSizeBytes} bytes, sha256 {manifest.DumpSha256}).");
        Console.WriteLine($"Covers {manifest.Schemas.Count} schemas from PostgreSQL {manifest.ServerVersion}.");
        Console.WriteLine("Treat the dump as a credential store: it contains delivery-channel URLs, indexer");
        Console.WriteLine("addresses, password verifiers and session token hashes. It is not encrypted.");
        return 0;
    }

    private static int List(BackupStore store)
    {
        var sets = store.List();

        if (sets.Count == 0)
        {
            Console.WriteLine($"No complete backups in {store.Root}.");
            return 0;
        }

        Console.WriteLine($"{sets.Count} backup(s) in {store.Root}, newest first:");
        foreach (var set in sets)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {set.Stamp}  {set.CreatedAt:u}  {set.DumpSizeBytes,14:N0} bytes"));
        }

        return 0;
    }

    private async Task<int> VerifyAsync(
        IServiceProvider scope,
        BackupStore store,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(store, out var set))
        {
            return UsageExitCode;
        }

        var service = scope.GetRequiredService<BackupService>();
        var outcome = await service.VerifyAsync(set, cancellationToken);

        if (outcome.IsFailure)
        {
            Console.Error.WriteLine($"Backup '{set.Stamp}' is not usable [{outcome.Error.Code}]: {outcome.Error.Message}");
            return 1;
        }

        Console.WriteLine($"Backup '{set.Stamp}' is intact ({outcome.Value.DumpSizeBytes} bytes, sha256 matches).");
        return 0;
    }

    private async Task<int> CheckAsync(
        IServiceProvider scope,
        BackupStore store,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(store, out var set))
        {
            return UsageExitCode;
        }

        var service = scope.GetRequiredService<BackupService>();
        var verified = await service.VerifyAsync(set, cancellationToken);
        if (verified.IsFailure)
        {
            Console.Error.WriteLine($"REFUSED [{verified.Error.Code}]: {verified.Error.Message}");
            return 1;
        }

        var inventory = scope.GetRequiredService<SchemaInventory>();
        var report = RestoreCompatibility.Check(
            verified.Value,
            inventory.ReadKnown(),
            await TryReadServerVersionAsync(scope, cancellationToken));

        Console.WriteLine(report.Summary);
        foreach (var detail in report.Details)
        {
            Console.WriteLine($"  - {detail}");
        }

        if (report.IsCompatible)
        {
            Console.WriteLine($"Backup '{set.Stamp}' may be restored into this build.");
            return 0;
        }

        Console.Error.WriteLine($"REFUSED [{report.RefusalCode}]: do not restore '{set.Stamp}' into this build.");
        return 1;
    }

    /// <summary>
    /// The target server's version, or <c>null</c> when it cannot be read. A check against a database
    /// that is not up yet is still worth running — it just cannot answer the version question, and says so.
    /// </summary>
    private static async Task<string?> TryReadServerVersionAsync(
        IServiceProvider scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = scope.GetRequiredService<Npgsql.NpgsqlConnection>();
            await connection.OpenAsync(cancellationToken);
            var version = connection.PostgreSqlVersion.ToString();
            await connection.CloseAsync();
            return version;
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or InvalidOperationException)
        {
            Console.WriteLine("The target server could not be reached; its PostgreSQL version was not checked.");
            return null;
        }
    }

    /// <summary>
    /// Resolves the named backup through the store's closed name pattern. A name is never treated as a
    /// path: the store only ever answers with a set inside its own root, so there is nothing to traverse.
    /// </summary>
    private bool TryResolve(BackupStore store, out BackupSet set)
    {
        set = null!;

        if (string.IsNullOrWhiteSpace(Name))
        {
            Console.Error.WriteLine($"Usage: {RootVerb} {Verb} <backup-name>. Run '{RootVerb} list' to see the names.");
            return false;
        }

        var found = store.Find(Name);
        if (found is null)
        {
            Console.Error.WriteLine($"No complete backup named '{Name}' in {store.Root}.");
            return false;
        }

        set = found;
        return true;
    }
}
