#!/usr/bin/env bash
#
# Schema ownership gate.
#
# "A module owns its tables and never touches another module's tables" is a rule nothing enforced.
# Run this against a database that migrate-all.sh has just built, and it becomes mechanical:
#
#   1. Every EF model snapshot declares exactly one schema, and every table it maps names that same
#      schema. A module that maps a table into another module's schema fails here — it adds no new
#      schema name and no foreign key, so nothing else would notice.
#   2. No two snapshots claim the same schema. Two modules sharing one namespace is the other way
#      the tree can look tidy while ownership is gone.
#   3. The schemas in the database are exactly the declared ones. A schema that appears from
#      nowhere, or a module whose migrations silently did not run, fails here.
#   4. Nothing lands in "public". A context that simply forgot HasDefaultSchema puts its tables in
#      the namespace every other module can reach, and would otherwise be invisible to check 3.
#   5. No foreign key crosses a schema boundary: cross-module identities are carried as opaque ids,
#      never as a physical reference.
#
# What it still does not cover: a raw SQL statement that names another module's schema, and a
# module deciding to own a second schema in a brand-new snapshot. Both need a reviewer.
#
# psql runs inside the database container over its local socket, so no credential is passed and
# none appears in the workflow, the logs or this file. Override CINOMNI_PSQL to point somewhere
# else (it must accept "-d <database> -tAc <sql>").
#
# Usage: bash .github/scripts/check-schemas.sh <database> [repository-root]
set -euo pipefail

database="${1:-}"
root="${2:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

if [ -z "$database" ]; then
  printf 'Usage: %s <database> [repository-root]\n' "$0" >&2
  exit 2
fi

psql_command="${CINOMNI_PSQL:-docker compose -f docker-compose.dev.yml exec -T postgres psql -U cinomni}"

run_sql() {
  # shellcheck disable=SC2086 # psql_command is an intentional command line, not a single word.
  $psql_command -d "$database" -tAc "$1"
}

failed=0

snapshots="$(
  find src -name '*ModelSnapshot.cs' -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort
)"

if [ -z "$snapshots" ]; then
  printf 'Found no EF model snapshots under src/, so there is nothing to verify. This is a bug in the gate.\n' >&2
  exit 1
fi

# One "<schema> <snapshot>" line per snapshot, which is both the expected-schema list and the
# ownership map. A snapshot that declares no schema, or more than one, never reaches it.
ownership=''

while IFS= read -r snapshot; do
  [ -n "$snapshot" ] || continue

  # "|| true": no match is the case this gate exists to report, not a reason to abort silently.
  declared="$(
    grep -ho 'HasDefaultSchema("[^"]*")' "$snapshot" |
      sed 's/HasDefaultSchema("//; s/")$//' |
      LC_ALL=C sort -u || true
  )"
  declared_count="$(printf '%s' "$declared" | grep -c . || true)"

  if [ "$declared_count" -eq 0 ]; then
    printf 'NO SCHEMA DECLARED: %s\n' "$snapshot" >&2
    printf '  Its DbContext has no HasDefaultSchema, so its tables land in "public", where every\n' >&2
    printf '  other module can reach them. Give the module its own schema.\n\n' >&2
    failed=1
    continue
  fi

  if [ "$declared_count" -gt 1 ]; then
    printf 'MORE THAN ONE SCHEMA DECLARED: %s\n%s\n' "$snapshot" "$declared" >&2
    printf '  A module owns exactly one schema.\n\n' >&2
    failed=1
    continue
  fi

  # Every table this snapshot maps must name its own schema, not another module's.
  foreign_tables="$(
    grep -ho 'ToTable("[^"]*", *"[^"]*")' "$snapshot" |
      sed 's/ToTable("//; s/", *"/ -> /; s/")$//' |
      grep -v -- " -> $declared\$" || true
  )"

  if [ -n "$foreign_tables" ]; then
    printf 'TABLE MAPPED OUTSIDE ITS OWN SCHEMA ("%s"): %s\n%s\n\n' \
      "$declared" "$snapshot" "$foreign_tables" >&2
    failed=1
  fi

  ownership="${ownership}${declared} ${snapshot}
"
done <<EOF
$snapshots
EOF

if [ "$failed" -ne 0 ]; then
  printf 'Schema ownership check failed before touching the database.\n' >&2
  exit 1
fi

duplicates="$(printf '%s' "$ownership" | awk '{print $1}' | LC_ALL=C sort | uniq -d)"
if [ -n "$duplicates" ]; then
  printf 'SCHEMA CLAIMED BY MORE THAN ONE MODULE:\n' >&2
  while IFS= read -r schema; do
    [ -n "$schema" ] || continue
    printf '%s\n' "$ownership" | awk -v s="$schema" '$1 == s { printf "  %s: %s\n", $1, $2 }' >&2
  done <<EOF
$duplicates
EOF
  printf '  Two modules sharing one schema is two modules sharing tables.\n\n' >&2
  exit 1
fi

# comm needs both sides ordered the same way, and PostgreSQL orders by the database collation.
expected="$(printf '%s' "$ownership" | awk '{print $1}' | LC_ALL=C sort)"

if ! actual="$(
  run_sql "select nspname from pg_namespace
           where nspname not in ('public', 'information_schema')
             and nspname not like 'pg\\_%';" | tr -d '\r' | sed '/^$/d' | LC_ALL=C sort
)"; then
  printf 'Could not read the schemas of database "%s". Is it there, and does CINOMNI_PSQL reach it?\n' \
    "$database" >&2
  exit 1
fi

printf 'Declared schemas (%s):\n%s\n\n' "$(printf '%s\n' "$expected" | wc -l | tr -d ' ')" "$expected"
printf 'Schemas in %s:\n%s\n\n' "$database" "$actual"

missing="$(comm -23 <(printf '%s\n' "$expected") <(printf '%s\n' "$actual"))"
if [ -n "$missing" ]; then
  printf 'MISSING SCHEMAS (the module declares them but the migrations did not create them):\n%s\n\n' \
    "$missing" >&2
  failed=1
fi

unexpected="$(comm -13 <(printf '%s\n' "$expected") <(printf '%s\n' "$actual"))"
if [ -n "$unexpected" ]; then
  printf 'UNEXPECTED SCHEMAS (present in the database, owned by no module):\n%s\n\n' "$unexpected" >&2
  failed=1
fi

# A relation in "public" belongs to no module and is reachable by all of them. The one exception is
# EF's own migration history: all 16 contexts share public.__EFMigrationsHistory today, because none
# of them configures MigrationsHistoryTable. It works — migration ids are unique across modules —
# but it is a shared object none of them owns, and moving it per schema is an owner decision with a
# data-migration cost, not something this gate can decide. Anything else in public fails.
public_relations="$(
  run_sql "select relkind::text || ' ' || relname
           from pg_class
           where relnamespace = 'public'::regnamespace
             and relkind in ('r', 'p', 'v', 'm')
             and relname <> '__EFMigrationsHistory'
           order by relname;" | tr -d '\r' | sed '/^$/d'
)"

if [ -n "$public_relations" ]; then
  printf 'RELATIONS IN "public" (owned by no module, reachable by every module):\n%s\n\n' \
    "$public_relations" >&2
  printf '  A DbContext without HasDefaultSchema puts its tables here. Give the module its schema.\n\n' >&2
  failed=1
fi

# A foreign key that crosses a schema is a module reaching into another module's tables.
cross_schema_fks="$(
  run_sql "select c.conname || ': ' ||
                  t.relnamespace::regnamespace || '.' || t.relname || ' -> ' ||
                  r.relnamespace::regnamespace || '.' || r.relname
           from pg_constraint c
           join pg_class t on t.oid = c.conrelid
           join pg_class r on r.oid = c.confrelid
           where c.contype = 'f' and t.relnamespace <> r.relnamespace
           order by 1;" | tr -d '\r' | sed '/^$/d'
)"

if [ -n "$cross_schema_fks" ]; then
  printf 'CROSS-SCHEMA FOREIGN KEYS (a module must not reference another module physically):\n%s\n\n' \
    "$cross_schema_fks" >&2
  failed=1
fi

if [ "$failed" -ne 0 ]; then
  printf 'Schema ownership check failed.\n' >&2
  exit 1
fi

printf 'Schema ownership OK: one schema per module, all of them present, nothing in "public", no foreign key across one.\n'
