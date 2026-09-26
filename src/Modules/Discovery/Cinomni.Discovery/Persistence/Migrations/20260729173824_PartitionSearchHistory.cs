using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cinomni.Discovery.Persistence.Migrations
{
    /// <summary>
    /// Turns the two ephemeral search-history tables into declaratively partitioned tables so the
    /// retention job can drop a whole month in O(1) instead of deleting millions of rows.
    /// <para>
    /// Written by hand: EF Core has no partitioning API, and the scaffolded version defaulted
    /// <c>found_at</c> to <c>0001-01-01</c>, which would have aged every existing result out on the
    /// first purge. The order below is expand/contract and safe on an installation that already
    /// holds rows — the whole migration runs in one transaction, so a failure at any point leaves
    /// the old heap tables exactly as they were:
    /// </para>
    /// <list type="number">
    ///   <item>expand: add <c>search_results.found_at</c> and backfill it from the parent execution
    ///   (same schema, no cross-schema access);</item>
    ///   <item>build the partitioned twins, one monthly partition for every month present in the
    ///   data plus the current month and the next two, and a DEFAULT partition so an insert can
    ///   never fail for want of a range;</item>
    ///   <item>copy the rows;</item>
    ///   <item>contract: drop the heap tables and rename the twins into place, then recreate the
    ///   primary keys and indexes under their final names.</item>
    /// </list>
    /// <para>
    /// Two schema facts change permanently. The primary keys become <c>(id, started_at)</c> and
    /// <c>(id, found_at)</c>, because PostgreSQL requires every unique constraint on a partitioned
    /// table to contain the partition key. And the foreign key between results and executions is
    /// gone: it would have forced the child to carry the parent's partition key too. Co-partitioning
    /// both tables on the same monthly boundary is what replaces it — <c>SearchPartitions</c> drops
    /// the execution month and the result month together.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public partial class PartitionSearchHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Expand: the child needs its own time reference, because after partitioning it can
            //    no longer reach its parent through a foreign key.
            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_results
                    ADD COLUMN found_at timestamp with time zone NOT NULL DEFAULT now();
                """);

            migrationBuilder.Sql(
                """
                UPDATE discovery.search_results AS r
                SET found_at = e.started_at
                FROM discovery.search_executions AS e
                WHERE e.id = r.execution_id;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_results ALTER COLUMN found_at DROP DEFAULT;
                """);

            // 2. Partitioned twins. LIKE copies columns, types, NOT NULL and defaults but no indexes
            //    or constraints — the old primary key on id alone is illegal on a partitioned table.
            migrationBuilder.Sql(
                """
                CREATE TABLE discovery.search_executions_partitioned
                    (LIKE discovery.search_executions INCLUDING DEFAULTS)
                    PARTITION BY RANGE (started_at);
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE discovery.search_results_partitioned
                    (LIKE discovery.search_results INCLUDING DEFAULTS)
                    PARTITION BY RANGE (found_at);
                """);

            // 3. One monthly partition per month the data actually occupies, plus the current month
            //    and the next two so routine inserts never land in DEFAULT. Every generated
            //    identifier comes from a date computed by the server from its own columns; nothing
            //    external reaches this DDL. Bounds are written with an explicit +00 offset so the
            //    month boundary does not move with the server's TimeZone setting.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    month_start date;
                    lower_bound text;
                    upper_bound text;
                BEGIN
                    FOR month_start IN
                        SELECT DISTINCT date_trunc('month', started_at AT TIME ZONE 'UTC')::date
                        FROM discovery.search_executions
                        UNION
                        SELECT DISTINCT date_trunc('month', found_at AT TIME ZONE 'UTC')::date
                        FROM discovery.search_results
                        UNION
                        SELECT (date_trunc('month', now() AT TIME ZONE 'UTC')
                                + (offset_months || ' month')::interval)::date
                        FROM generate_series(0, 2) AS offset_months
                    LOOP
                        lower_bound := to_char(month_start, 'YYYY-MM-DD') || ' 00:00:00+00';
                        upper_bound := to_char(month_start + interval '1 month', 'YYYY-MM-DD')
                                       || ' 00:00:00+00';

                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS discovery.%I '
                            || 'PARTITION OF discovery.search_executions_partitioned '
                            || 'FOR VALUES FROM (%L) TO (%L)',
                            'search_executions_' || to_char(month_start, 'YYYY_MM'),
                            lower_bound,
                            upper_bound);

                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS discovery.%I '
                            || 'PARTITION OF discovery.search_results_partitioned '
                            || 'FOR VALUES FROM (%L) TO (%L)',
                            'search_results_' || to_char(month_start, 'YYYY_MM'),
                            lower_bound,
                            upper_bound);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE discovery.search_executions_default
                    PARTITION OF discovery.search_executions_partitioned DEFAULT;
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE discovery.search_results_default
                    PARTITION OF discovery.search_results_partitioned DEFAULT;
                """);

            // 4. Copy. LIKE preserved the column order, so SELECT * lines up.
            migrationBuilder.Sql(
                """
                INSERT INTO discovery.search_executions_partitioned
                SELECT * FROM discovery.search_executions;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO discovery.search_results_partitioned
                SELECT * FROM discovery.search_results;
                """);

            // 5. Contract. The child goes first: dropping it removes the foreign key that would
            //    otherwise block the parent.
            migrationBuilder.Sql("DROP TABLE discovery.search_results;");
            migrationBuilder.Sql("DROP TABLE discovery.search_executions;");

            migrationBuilder.Sql(
                "ALTER TABLE discovery.search_executions_partitioned RENAME TO search_executions;");
            migrationBuilder.Sql(
                "ALTER TABLE discovery.search_results_partitioned RENAME TO search_results;");

            // 6. Keys and indexes under their final names, now that the heap tables no longer own
            //    them. An index on a partitioned parent is created on every partition automatically,
            //    including the ones SearchPartitions adds later.
            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_executions
                    ADD CONSTRAINT pk_search_executions PRIMARY KEY (id, started_at);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_results
                    ADD CONSTRAINT pk_search_results PRIMARY KEY (id, found_at);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_search_executions_started_at
                    ON discovery.search_executions (started_at);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_search_results_execution_id
                    ON discovery.search_results (execution_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse into plain heap tables. LIKE against a partitioned table yields a plain one.
            migrationBuilder.Sql(
                """
                CREATE TABLE discovery.search_executions_heap
                    (LIKE discovery.search_executions INCLUDING DEFAULTS);
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE discovery.search_results_heap
                    (LIKE discovery.search_results INCLUDING DEFAULTS);
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO discovery.search_executions_heap
                SELECT * FROM discovery.search_executions;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO discovery.search_results_heap
                SELECT * FROM discovery.search_results;
                """);

            // Dropping a partitioned parent drops every partition with it.
            migrationBuilder.Sql("DROP TABLE discovery.search_results;");
            migrationBuilder.Sql("DROP TABLE discovery.search_executions;");

            migrationBuilder.Sql(
                "ALTER TABLE discovery.search_executions_heap RENAME TO search_executions;");
            migrationBuilder.Sql(
                "ALTER TABLE discovery.search_results_heap RENAME TO search_results;");

            // found_at only existed to be a partition key.
            migrationBuilder.Sql("ALTER TABLE discovery.search_results DROP COLUMN found_at;");

            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_executions
                    ADD CONSTRAINT pk_search_executions PRIMARY KEY (id);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_results
                    ADD CONSTRAINT pk_search_results PRIMARY KEY (id);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_search_executions_started_at
                    ON discovery.search_executions (started_at);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_search_results_execution_id
                    ON discovery.search_results (execution_id);
                """);

            // A result whose execution is already gone cannot satisfy the restored foreign key.
            migrationBuilder.Sql(
                """
                DELETE FROM discovery.search_results AS r
                WHERE NOT EXISTS (
                    SELECT 1 FROM discovery.search_executions AS e WHERE e.id = r.execution_id);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE discovery.search_results
                    ADD CONSTRAINT fk_search_results_search_executions_execution_id
                    FOREIGN KEY (execution_id) REFERENCES discovery.search_executions (id)
                    ON DELETE CASCADE;
                """);
        }
    }
}
