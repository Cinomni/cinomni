#!/usr/bin/env bash
#
# What scripts/cinomni-restore.sh refuses, and what it lets through.
#
# The guard that matters is "the application must be stopped": a restore under a running Host replaces
# the database its outbox relay, command worker and scheduler are driving. That guard has to hold in
# both places the script runs — on the host, and inside the application container, which is the only
# place pg_restore exists in the packaged deployment, and where the default loopback probe can never
# see the running application.
#
# The other guard here is "the target must be empty". It has to fail closed for the same reason: a
# count query whose status was never established says nothing about the database, and treating no
# answer as a zero would let a restore land on top of a populated installation.
#
# pg_restore, psql, curl and sha256sum are stubbed on PATH, so this exercises the script's decisions
# and touches no database. Run it from anywhere:
#
#   bash tests/scripts/restore-guard-tests.sh
#
set -uo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
script="$root/scripts/cinomni-restore.sh"
[ -f "$script" ] || { printf 'Not found: %s\n' "$script" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
passes=0

# ---------------------------------------------------------------------------
# A backup that hashes to what its manifest says, so every run below gets past the integrity check
# and reaches the guard under test.
# ---------------------------------------------------------------------------
backup="$work/backups"
mkdir -p "$backup" "$work/bin"
printf 'not a real archive\n' >"$backup/cinomni-20260101-000000.dump"
dump_sha="$(sha256sum "$backup/cinomni-20260101-000000.dump" | cut -d' ' -f1)"
cat >"$backup/cinomni-20260101-000000.manifest.json" <<MANIFEST
{
  "formatVersion": 1,
  "createdAt": "2026-01-01T00:00:00Z",
  "dumpFileName": "cinomni-20260101-000000.dump",
  "dumpSha256": "$dump_sha",
  "databaseName": "cinomni",
  "serverVersion": "16.4",
  "schemas": [ { "name": "identity" }, { "name": "catalog" } ]
}
MANIFEST
manifest="$backup/cinomni-20260101-000000.manifest.json"

# The same manifest, and an archive that is not the one it was written for.
printf 'a truncated or substituted copy\n' >"$backup/tampered.dump"

# A manifest file that exists and holds nothing, which is what an interrupted copy leaves behind.
: >"$backup/empty.manifest.json"

# ---------------------------------------------------------------------------
# Stubs. CURL_EXIT and PSQL_SESSIONS script what the two probes report; PSQL_EXIT makes psql itself
# fail, which is the "neither question could be answered" case. PSQL_SCHEMAS and PSQL_SCHEMA_EXIT do
# the same for the empty-target check alone, so a run can answer one question and fail the other.
# ---------------------------------------------------------------------------
cat >"$work/bin/curl" <<'STUB'
#!/bin/sh
exit "${CURL_EXIT:-7}"
STUB

cat >"$work/bin/psql" <<'STUB'
#!/bin/sh
[ "${PSQL_EXIT:-0}" -eq 0 ] || exit "${PSQL_EXIT}"
# The empty-target check asks for a schema count; everything else here asks for a session count.
case "$*" in
    *information_schema.schemata*)
        if [ "${PSQL_SCHEMA_EXIT:-0}" -ne 0 ]; then
            # What a real client prints when it cannot run the query: an error on stderr, nothing on
            # stdout, and a non-zero status that only the caller's own check can see.
            printf 'psql: error: connection to server failed\n' >&2
            exit "${PSQL_SCHEMA_EXIT}"
        fi
        printf '%s\n' "${PSQL_SCHEMAS:-0}"
        ;;
    *pg_stat_activity*)            printf '%s\n' "${PSQL_SESSIONS:-0}" ;;
    *)                             printf '\n' ;;
esac
STUB

cat >"$work/bin/pg_restore" <<'STUB'
#!/bin/sh
printf 'pg_restore stub ran\n'
exit 0
STUB

chmod +x "$work/bin/curl" "$work/bin/psql" "$work/bin/pg_restore"

# ---------------------------------------------------------------------------
# One case: run the script with the given extra flags and environment, and assert its exit code plus
# what its output does (or does not) mention.
#
# --recreate is NOT in the base arguments: a case that passes it never reaches the empty-target check,
# and that check is half of what is tested here. Each case says which of the two paths it is on.
#
#   run_case <name> <exit> <must say> <must not say> <extra flags> [VAR=value ...]
# ---------------------------------------------------------------------------
run_case() {
    name="$1"; expected_status="$2"; expect="$3"; reject="$4"; extra="$5"; shift 5

    # shellcheck disable=SC2086 # $extra is a deliberate, test-owned list of flags.
    output="$(env PATH="$work/bin:$PATH" PGPASSWORD=stub "$@" \
        sh "$script" --manifest "$manifest" --host db --port 5432 --username cinomni \
        --database cinomni --checked --yes $extra 2>&1)"
    status=$?

    if [ "$status" -ne "$expected_status" ]; then
        printf 'FAIL %s: expected exit %s, got %s\n%s\n\n' "$name" "$expected_status" "$status" "$output" >&2
        failures=$((failures + 1))
        return
    fi

    if [ -n "$expect" ] && ! printf '%s' "$output" | grep -qF -- "$expect"; then
        printf 'FAIL %s: output does not mention "%s"\n%s\n\n' "$name" "$expect" "$output" >&2
        failures=$((failures + 1))
        return
    fi

    if [ -n "$reject" ] && printf '%s' "$output" | grep -qF -- "$reject"; then
        printf 'FAIL %s: output must not mention "%s"\n%s\n\n' "$name" "$reject" "$output" >&2
        failures=$((failures + 1))
        return
    fi

    printf 'ok   %s\n' "$name"
    passes=$((passes + 1))
}

# ---------------------------------------------------------------------------
# "The application must be stopped."
# ---------------------------------------------------------------------------

# An answering health endpoint is a running application, whatever its status code: a Host that reports
# 503 because a dependency is down still has its relay and scheduler driving the database.
run_case 'an answering health endpoint refuses' 1 'still running' 'pg_restore stub ran' \
    '--recreate' CURL_EXIT=0

# The container case. The probe cannot connect, which is not proof of anything, so the conclusive
# question is asked instead: is anything attached to the database about to be replaced?
run_case 'an unreachable probe with sessions on the database refuses' \
    1 'still attached' 'pg_restore stub ran' '--recreate' CURL_EXIT=7 PSQL_SESSIONS=2

run_case 'an unreachable probe with no session proceeds' \
    0 'No client session is attached' '' '--recreate' CURL_EXIT=7 PSQL_SESSIONS=0

run_case 'an unreachable probe says it is not proof' \
    0 'not proof that nothing is running' '' '--recreate' CURL_EXIT=7 PSQL_SESSIONS=0

# Fails closed: neither question could be answered, so nothing is restored.
run_case 'an unreachable probe and an unusable database refuse' \
    1 'Neither question was answered' 'pg_restore stub ran' '--recreate' CURL_EXIT=7 PSQL_EXIT=2

# That refusal has to say what to do about it. The session question is asked over a connection to the
# maintenance database on EVERY run, so a role without CONNECT there meets this guard as its first
# obstacle, and the way past it is the flag rather than a puzzle.
run_case 'the refusal names the maintenance database as the way out' \
    1 'pass --maintenance-db' 'pg_restore stub ran' '--recreate' CURL_EXIT=7 PSQL_EXIT=2

# The override still exists, and it skips both questions rather than only the HTTP one.
run_case '--no-health-check skips both questions' \
    0 'Not probing for a running application' 'Asking the database' '--recreate --no-health-check' \
    CURL_EXIT=0 PSQL_SESSIONS=9

# ---------------------------------------------------------------------------
# "The target must be empty." Without --recreate, so the count below is what decides.
# ---------------------------------------------------------------------------

run_case 'an empty target proceeds' \
    0 'pg_restore stub ran' 'REFUSED' '' CURL_EXIT=7 PSQL_SESSIONS=0 PSQL_SCHEMAS=0

run_case 'a target that still holds schemas refuses' \
    1 'already holds 3 schema(s)' 'pg_restore stub ran' '' \
    CURL_EXIT=7 PSQL_SESSIONS=0 PSQL_SCHEMAS=3

# The fail-closed case. The count query failed, so nothing is known about the target — and a database
# that could not be inspected must not be treated as an empty one. Piped into a filter, this query's
# status was the filter's, its output an empty string, and the empty string a zero.
run_case 'a schema count that could not be run refuses instead of assuming empty' \
    1 'could not be inspected' 'pg_restore stub ran' '' \
    CURL_EXIT=7 PSQL_SESSIONS=0 PSQL_SCHEMA_EXIT=2

run_case 'a schema count that is not a number refuses' \
    1 'is not a number' 'pg_restore stub ran' '' \
    CURL_EXIT=7 PSQL_SESSIONS=0 PSQL_SCHEMAS=unavailable

# --maintenance-db is the answer to a role without CONNECT on `postgres`, which makes the target
# itself the obvious wrong thing to name with --recreate. Refused here rather than half-way through.
run_case '--recreate refuses the target as its own maintenance database' \
    1 'cannot also be the maintenance database' 'pg_restore stub ran' \
    '--recreate --maintenance-db cinomni' CURL_EXIT=7 PSQL_SESSIONS=0

# ---------------------------------------------------------------------------
# Reading the backup itself.
# ---------------------------------------------------------------------------

# An unchanged guard, kept here so a change to the probe cannot quietly weaken it: the integrity check
# runs before either question is asked, so a bad copy never reaches them.
run_case 'a dump that does not match its manifest refuses' \
    1 'Do not restore it' 'pg_restore stub ran' "--recreate --dump $backup/tampered.dump" \
    CURL_EXIT=7 PSQL_SESSIONS=0

# A manifest read that produced nothing is reported as that, not carried forward as a set of empty
# fields. The later --manifest wins, which is how this case replaces the good one.
run_case 'a manifest that reads as nothing refuses' \
    1 'is empty' 'pg_restore stub ran' "--recreate --manifest $backup/empty.manifest.json" \
    CURL_EXIT=7 PSQL_SESSIONS=0

printf '\n%s passed, %s failed\n' "$passes" "$failures"
[ "$failures" -eq 0 ] || exit 1
