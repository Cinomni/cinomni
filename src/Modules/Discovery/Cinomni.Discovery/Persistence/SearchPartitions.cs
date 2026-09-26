using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Discovery.Persistence;

/// <summary>The two range-partitioned search-history tables, as a closed set.</summary>
public enum SearchHistoryTable
{
    /// <summary><c>discovery.search_executions</c>, partitioned on <c>started_at</c>.</summary>
    Executions,

    /// <summary><c>discovery.search_results</c>, partitioned on <c>found_at</c>.</summary>
    Results,
}

/// <summary>What one pass of <see cref="SearchPartitions.EnsureAsync"/> achieved.</summary>
/// <param name="Created">Partitions created in this pass.</param>
/// <param name="Blocked">
/// Partitions that could not be attached because the DEFAULT partition already holds rows of that
/// month. PostgreSQL validates the default when a new range is attached, so this is a state the
/// installation has to be told about — but never one that may stop it: the DEFAULT partition keeps
/// accepting those inserts, so searches carry on working while an operator relocates the rows.
/// </param>
public sealed record SearchPartitionMaintenance(int Created, IReadOnlyList<string> Blocked);

/// <summary>
/// Monthly partition maintenance for the search history: creates the partitions the next inserts
/// will need, and drops whole months that have aged out. Dropping a partition reclaims the space
/// immediately and in constant time, which is the entire reason these two tables are partitioned
/// rather than swept with a DELETE.
/// <para>
/// This is the only code in Cinomni that emits DDL at run time, so it is deliberately closed:
/// the schema and both table names are compile-time constants, the month comes from a
/// <see cref="DateOnly"/>, and every identifier is produced by <see cref="PartitionName"/>. No
/// configuration value, request value or provider string can reach these statements. When a name is
/// read back from the catalogue it is never used directly — it is parsed into a month and the name
/// is regenerated, so a table that does not round-trip (the DEFAULT partition, or anything a human
/// created by hand) is skipped rather than dropped.
/// </para>
/// <para>
/// EF Core cannot express partitioning, so these statements are raw SQL by necessity rather than as
/// an optimisation. The only parameters are the constant schema and table names, and they are bound,
/// not interpolated.
/// </para>
/// </summary>
public static class SearchPartitions
{
    /// <summary>The module's own schema. Never configurable — see the type remarks.</summary>
    public const string Schema = "discovery";

    private const string ExecutionsTable = "search_executions";
    private const string ResultsTable = "search_results";
    private const string MonthFormat = "yyyy_MM";
    private const string DefaultSuffix = "_default";

    /// <summary>How many months ahead of the current one are pre-created, so DEFAULT stays empty.</summary>
    public const int MonthsCreatedAhead = 2;

    /// <summary>The physical name of the partition holding <paramref name="month"/>.</summary>
    /// <param name="table">Which partitioned parent; a closed set, not a name.</param>
    /// <param name="month">Any day in the month; only its year and month are used.</param>
    public static string PartitionName(SearchHistoryTable table, DateOnly month) =>
        $"{ParentName(table)}_{FirstOfMonth(month).ToString(MonthFormat, CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Creates the partitions for <paramref name="currentMonth"/> and the following
    /// <see cref="MonthsCreatedAhead"/> months on both tables, if they do not already exist.
    /// Idempotent, so it runs on every retention tick.
    /// <para>
    /// A month that cannot be attached because the DEFAULT partition already holds rows of that month
    /// is <b>reported, not thrown</b>: it is a recoverable condition (the default keeps accepting the
    /// inserts) and one blocked month must not stop the remaining months, the retention drop, or the
    /// process that called this at startup.
    /// </para>
    /// </summary>
    public static async Task<SearchPartitionMaintenance> EnsureAsync(
        DiscoveryDbContext dbContext,
        DateOnly currentMonth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var created = 0;
        var blocked = new List<string>();
        var first = FirstOfMonth(currentMonth);

        for (var offset = 0; offset <= MonthsCreatedAhead; offset++)
        {
            var month = first.AddMonths(offset);

            foreach (var table in new[] { SearchHistoryTable.Executions, SearchHistoryTable.Results })
            {
                var outcome = await CreateAsync(dbContext, table, month, cancellationToken);
                if (outcome == PartitionOutcome.Created)
                {
                    created++;
                }
                else if (outcome == PartitionOutcome.Blocked)
                {
                    blocked.Add(PartitionName(table, month));
                }
            }
        }

        return new SearchPartitionMaintenance(created, blocked);
    }

    /// <summary>
    /// Drops every partition whose month lies entirely before <paramref name="oldestMonthToKeep"/>,
    /// on both tables. Executions and results are co-partitioned on the same boundary, so both sides
    /// of a month disappear together — that is what replaces the foreign key the partitioning removed.
    /// </summary>
    /// <returns>The names of the partitions that were dropped, in the order they were dropped.</returns>
    public static async Task<IReadOnlyList<string>> DropExpiredAsync(
        DiscoveryDbContext dbContext,
        DateOnly oldestMonthToKeep,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var keepFrom = FirstOfMonth(oldestMonthToKeep);
        var dropped = new List<string>();

        // Results first: a result outliving its execution is the failure this ordering avoids if the
        // second statement is interrupted.
        foreach (var table in new[] { SearchHistoryTable.Results, SearchHistoryTable.Executions })
        {
            foreach (var month in await ExpiredMonthsAsync(dbContext, table, keepFrom, cancellationToken))
            {
                var name = PartitionName(table, month);
                await ExecuteDdlAsync(
                    dbContext,
                    $"DROP TABLE IF EXISTS {Schema}.{Identifier(name)}",
                    cancellationToken);
                dropped.Add(name);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Rows sitting in a DEFAULT partition, per table. Always zero on a healthy installation: a
    /// non-zero count means a row landed outside every declared month, and the next
    /// <see cref="EnsureAsync"/> for that month would then have to lock and scan the default.
    /// </summary>
    public static async Task<(long Executions, long Results)> CountDefaultRowsAsync(
        DiscoveryDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var executions = await CountDefaultRowsAsync(dbContext, SearchHistoryTable.Executions, cancellationToken);
        var results = await CountDefaultRowsAsync(dbContext, SearchHistoryTable.Results, cancellationToken);
        return (executions, results);
    }

    /// <summary>
    /// The months the rows sitting in a DEFAULT partition belong to, as <c>yyyy_MM</c>, across both
    /// tables and without repetition. Only worth calling once
    /// <see cref="CountDefaultRowsAsync(DiscoveryDbContext, CancellationToken)"/> reported something:
    /// it turns "some rows are outside every month" into the list an operator needs to relocate them.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DefaultMonthsAsync(
        DiscoveryDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var months = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var table in new[] { SearchHistoryTable.Executions, SearchHistoryTable.Results })
        {
            var name = ParentName(table) + DefaultSuffix;
            if (!await ExistsAsync(dbContext, name, cancellationToken))
            {
                continue;
            }

            // Both identifiers are compile-time constants selected by a closed enum; there is no
            // value to bind and nothing external reaches the statement.
#pragma warning disable EF1002 // Constant identifiers only; see the type remarks.
            var found = await dbContext.Database
                .SqlQueryRaw<string>(
                    $"""
                     SELECT DISTINCT
                         to_char({PartitionColumn(table)} AT TIME ZONE 'UTC', 'YYYY_MM') AS "Value"
                     FROM {Schema}.{Identifier(name)}
                     """)
                .ToListAsync(cancellationToken);
#pragma warning restore EF1002

            months.UnionWith(found);
        }

        return [.. months];
    }

    private static async Task<long> CountDefaultRowsAsync(
        DiscoveryDbContext dbContext,
        SearchHistoryTable table,
        CancellationToken cancellationToken)
    {
        var name = ParentName(table) + DefaultSuffix;

        // The DEFAULT partition is created by the migration, but an installation restored from an
        // older dump may not have it; a missing default is not an error, it just cannot hold rows.
        if (!await ExistsAsync(dbContext, name, cancellationToken))
        {
            return 0;
        }

        var counts = await QueryLongAsync(
            dbContext,
            $"SELECT count(*) AS \"Value\" FROM {Schema}.{Identifier(name)}",
            cancellationToken);

        return counts;
    }

    /// <summary>How one month's partition ended up, so a blocked month can be reported rather than thrown.</summary>
    private enum PartitionOutcome
    {
        /// <summary>The partition was already there (or another connection won the race).</summary>
        Present,

        /// <summary>This call created it.</summary>
        Created,

        /// <summary>Rows for that month already sit in the DEFAULT partition, so it cannot be attached.</summary>
        Blocked,
    }

    private static async Task<PartitionOutcome> CreateAsync(
        DiscoveryDbContext dbContext,
        SearchHistoryTable table,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var name = PartitionName(table, month);

        // Existence is checked first rather than relying on IF NOT EXISTS alone, so the caller can
        // report what it actually created and an existing month costs no ACCESS EXCLUSIVE lock.
        if (await ExistsAsync(dbContext, name, cancellationToken))
        {
            return PartitionOutcome.Present;
        }

        var lowerBound = Bound(month);
        var upperBound = Bound(month.AddMonths(1));

        try
        {
            await ExecuteDdlAsync(
                dbContext,
                $"""
                 CREATE TABLE IF NOT EXISTS {Schema}.{Identifier(name)}
                     PARTITION OF {Schema}.{ParentName(table)}
                     FOR VALUES FROM ('{lowerBound}') TO ('{upperBound}')
                 """,
                cancellationToken);

            return PartitionOutcome.Created;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // Attaching a range partition revalidates the DEFAULT partition, and PostgreSQL refuses
            // when a row already in DEFAULT belongs to the new range. Nothing here can fix that
            // safely — moving rows needs an exclusive window an operator decides on — but it must
            // never stop the remaining months or the caller. Inserts keep working through DEFAULT.
            return PartitionOutcome.Blocked;
        }
        catch (PostgresException failure)
            when (failure.SqlState is PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.UniqueViolation)
        {
            // Another connection created the same month between the existence check and the CREATE.
            // The desired state holds, so this is not a failure.
            return PartitionOutcome.Present;
        }
    }

    /// <summary>Whether a table of this schema exists, by generated name. Bound as a value, not interpolated.</summary>
    private static async Task<bool> ExistsAsync(
        DiscoveryDbContext dbContext,
        string name,
        CancellationToken cancellationToken)
    {
        var qualified = $"{Schema}.{name}";
        var present = await dbContext.Database
            .SqlQuery<bool>($"SELECT to_regclass({qualified}) IS NOT NULL AS \"Value\"")
            .ToListAsync(cancellationToken);

        return present.Count > 0 && present[0];
    }

    private static async Task<IReadOnlyList<DateOnly>> ExpiredMonthsAsync(
        DiscoveryDbContext dbContext,
        SearchHistoryTable table,
        DateOnly keepFrom,
        CancellationToken cancellationToken)
    {
        var parent = ParentName(table);

        var names = await dbContext.Database
            .SqlQuery<string>(
                $"""
                 SELECT child.relname AS "Value"
                 FROM pg_inherits AS inherits
                 JOIN pg_class AS child ON child.oid = inherits.inhrelid
                 JOIN pg_class AS parent ON parent.oid = inherits.inhparent
                 JOIN pg_namespace AS ns ON ns.oid = parent.relnamespace
                 WHERE ns.nspname = {Schema} AND parent.relname = {parent}
                 """)
            .ToListAsync(cancellationToken);

        var expired = new List<DateOnly>();

        foreach (var name in names)
        {
            // The catalogue name is evidence, never an identifier: it is accepted only if it
            // round-trips through PartitionName, and the regenerated name is what gets dropped.
            if (TryParseMonth(table, name, out var month) && month < keepFrom)
            {
                expired.Add(month);
            }
        }

        expired.Sort();
        return expired;
    }

    /// <summary>
    /// Recovers the month a generated partition name encodes. Returns <c>false</c> for the DEFAULT
    /// partition and for anything else that is not exactly what <see cref="PartitionName"/> produces.
    /// </summary>
    private static bool TryParseMonth(SearchHistoryTable table, string name, out DateOnly month)
    {
        month = default;

        var prefix = ParentName(table) + "_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name[prefix.Length..];
        if (!DateOnly.TryParseExact(
                suffix + "_01",
                MonthFormat + "_dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        // Round-trip: only a name this class could itself have produced is acted upon.
        if (!string.Equals(PartitionName(table, parsed), name, StringComparison.Ordinal))
        {
            return false;
        }

        month = parsed;
        return true;
    }

    /// <summary>
    /// The single place in the module that sends unparameterised SQL to the server. DDL cannot take
    /// parameters — an identifier is not a value — so every fragment reaching this method has to be
    /// a compile-time constant or a name that came out of <see cref="Identifier"/>.
    /// </summary>
    private static Task ExecuteDdlAsync(
        DiscoveryDbContext dbContext,
        string statement,
        CancellationToken cancellationToken) =>
#pragma warning disable EF1002 // DDL cannot be parameterised; identifiers are generated and validated.
        dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
#pragma warning restore EF1002

    private static async Task<long> QueryLongAsync(
        DiscoveryDbContext dbContext,
        string statement,
        CancellationToken cancellationToken)
    {
#pragma warning disable EF1002 // The table name is generated and validated; there is no value to bind.
        var values = await dbContext.Database.SqlQueryRaw<long>(statement).ToListAsync(cancellationToken);
#pragma warning restore EF1002
        return values.Count == 0 ? 0 : values[0];
    }

    /// <summary>
    /// Last line of defence before a name is concatenated into DDL: a generated partition name is
    /// lower-case ASCII letters, digits and underscores and nothing else. Everything that reaches
    /// here is already produced by <see cref="PartitionName"/>, so a failure means the generator
    /// itself was changed unsafely — hence an exception rather than a filtered result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is not a safe generated identifier.</exception>
    private static string Identifier(string name)
    {
        if (name.Length is 0 or > 63)
        {
            throw new InvalidOperationException("Refusing to emit DDL for an out-of-range identifier.");
        }

        foreach (var character in name)
        {
            var allowed = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
            if (!allowed)
            {
                throw new InvalidOperationException(
                    "Refusing to emit DDL for an identifier that was not generated by SearchPartitions.");
            }
        }

        return name;
    }

    private static string ParentName(SearchHistoryTable table) => table switch
    {
        SearchHistoryTable.Executions => ExecutionsTable,
        SearchHistoryTable.Results => ResultsTable,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown search-history table."),
    };

    /// <summary>The partition key of each parent. A compile-time constant per closed enum member.</summary>
    private static string PartitionColumn(SearchHistoryTable table) => table switch
    {
        SearchHistoryTable.Executions => "started_at",
        SearchHistoryTable.Results => "found_at",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown search-history table."),
    };

    /// <summary>A partition bound in UTC, so a month boundary never moves with the server time zone.</summary>
    private static string Bound(DateOnly month) =>
        FirstOfMonth(month).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00+00";

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);
}
