#!/bin/sh
# Restore a Cinomni database backup. POSIX sh; no bashisms.
#
# This is a script and not an endpoint on purpose. A restore drops and recreates every object in the
# database, so the application must be stopped for it: its outbox relay, command worker and job
# scheduler would otherwise be running against a half-restored database and could publish events out
# of it. And a "replace my whole installation" button reachable over HTTP is not a blast radius a
# self-hosted node should carry — the same reason there is no download route for a dump.
#
# Read DEPLOYMENT.md, "Backup and restore", before running this. The short version:
#
#   1. docker compose exec cinomni dotnet Cinomni.Host.dll backup check <name>
#   2. stop the application  (docker compose stop cinomni)
#   3. this script, from inside the application container
#   4. start it again and confirm /health
#
# Step 1 is the gate that refuses a dump the running build cannot read. Run it first, while the
# container is still up; against an already-stopped installation the same verb is
# `docker compose run --rm cinomni backup check <name>`. This script cannot run it for you — it does
# not know where your application lives — so it asks whether you did, and says what it is you are
# about to overwrite.
#
# WHERE THIS RUNS. In the packaged deployment the database publishes no host port and sits on an
# internal network, so nothing on the host can reach it and pg_restore is not installed there either.
# The application image carries pg_restore, psql, sha256sum and curl, and is already on that network,
# so this script is shipped inside it at /app/scripts and run there — DEPLOYMENT.md, section 6, has
# the exact command. It still works unchanged on a host that can reach the server directly.
#
# What the SHA-256 below proves, and what it does not: it detects a truncated or corrupted copy. It
# is NOT a signature. The expected hash lives in the manifest beside the archive, and neither file is
# signed, so anyone who can write both passes this check trivially. `backup check` is a compatibility
# gate on the same unsigned manifest. Restore only a dump from a location you control.
#
# The target database must be EMPTY. Restoring on top of an existing one is not offered, and not
# because it is untidy: pg_restore --clean drops a partitioned table's objects one at a time and
# PostgreSQL refuses to drop a partition's inherited primary key, so that restore dies half-way
# through with the database in neither state. Restoring into a database created for it has no such
# failure mode, and it leaves the old one intact until you are satisfied with the new one.
#
# The database password is read from the environment (PGPASSWORD, as libpq expects). It is never an
# argument: an argument vector is world-readable on this machine.
#
# A MAINTENANCE DATABASE IS ALWAYS NEEDED, on every run and not only with --recreate. The "is the
# application stopped" guard asks the server which sessions are attached to the database about to be
# replaced, and it has to ask that from somewhere else: the target may be dropped moments later, and
# may not even exist yet. So the restoring role needs CONNECT on it. It defaults to `postgres`; a role
# without CONNECT there passes --maintenance-db naming any database it can connect to, because
# pg_stat_activity is cluster-wide and every database answers the question identically.
#
# NO PIPE DECIDES ANYTHING HERE. `value=$(cmd | filter)` reports the filter's exit status, so a query
# that failed comes back as an empty string and a later `${value:-0}` reads it as a zero — a database
# that could not be inspected would then look empty and be restored into. `set -o pipefail` is not
# POSIX and this script runs under dash inside the application image, so every fallible command is run
# on its own with its status checked, and only the resulting text is filtered.

set -eu

usage() {
    cat >&2 <<'USAGE'
Usage: cinomni-restore.sh --manifest <path> [options]

  --manifest <path>   The .manifest.json written beside the dump. Required.
  --dump <path>       The archive. Defaults to the dumpFileName the manifest names, beside it.
  --database <name>   Database to restore into. It must be EMPTY. Defaults to the one the manifest
                      was taken from.
  --recreate          Drop and recreate that database first. This destroys it.
  --maintenance-db    Database to connect to in order to ask the server about the target, and to drop
                      and create it with --recreate. Default: postgres. Required on EVERY run - see
                      "Maintenance database" below.
  --host <host>       PostgreSQL host. Default: localhost, or PGHOST.
  --port <port>       PostgreSQL port. Default: 5432, or PGPORT.
  --username <name>   PostgreSQL role. Default: cinomni, or PGUSER.
  --checked           You have run 'backup check' against this manifest and it exited 0.
  --no-health-check   Do not probe for a running application. Only when you know it is stopped.
  --yes               Proceed. Without it this prints what it would do and stops.

Environment:
  PGPASSWORD            The role's password. Never pass a password as an argument.
  CINOMNI_HEALTH_URL    Probed to refuse a restore while the application is up. Default is built
                        from CINOMNI_BIND_ADDRESS and CINOMNI_HTTP_PORT (127.0.0.1:8080). Running
                        inside a container, set it to the service name — the default loopback is
                        that container's own, where nothing listens.
  CINOMNI_BIND_ADDRESS  Where the application is published, as in .env.
  CINOMNI_HTTP_PORT     The published port, as in .env.

The probe fails closed. Any HTTP answer refuses. A probe that reaches nothing is treated as "could
not tell", not as "nothing is running", so the database is then asked whether any client session is
still attached to it; if neither question can be answered, this refuses. --no-health-check is the way
to say you have checked yourself.

Maintenance database:
  EVERY run connects to it, not only --recreate. The guard above asks pg_stat_activity which sessions
  are attached to the target, and asks it from another database on purpose: the target may be dropped
  moments later, and may not exist yet. The restoring role therefore needs CONNECT on it.

  Default `postgres`. A role without CONNECT there is refused, and the answer is --maintenance-db
  naming any database that role CAN connect to - pg_stat_activity is cluster-wide, so every database
  answers identically. It must not be the database being restored when --recreate is used, since a
  database cannot be dropped from a session connected to it.

  There is no flag to skip only this. --no-health-check skips the whole guard, and it is a statement
  that you have confirmed the application is stopped yourself.

Exit codes: 0 restored, 1 refused or failed, 2 usage.
USAGE
}

fail() { printf '%s\n' "$*" >&2; exit 1; }

manifest=''
dump=''
database=''
maintenance_db='postgres'
host="${PGHOST:-localhost}"
port="${PGPORT:-5432}"
username="${PGUSER:-cinomni}"
confirmed=0
checked=0
recreate=0
skip_health_check=0

while [ $# -gt 0 ]; do
    case "$1" in
        --manifest)       [ $# -ge 2 ] || { usage; exit 2; }; manifest="$2"; shift 2 ;;
        --dump)           [ $# -ge 2 ] || { usage; exit 2; }; dump="$2"; shift 2 ;;
        --database)       [ $# -ge 2 ] || { usage; exit 2; }; database="$2"; shift 2 ;;
        --maintenance-db) [ $# -ge 2 ] || { usage; exit 2; }; maintenance_db="$2"; shift 2 ;;
        --host)           [ $# -ge 2 ] || { usage; exit 2; }; host="$2"; shift 2 ;;
        --port)           [ $# -ge 2 ] || { usage; exit 2; }; port="$2"; shift 2 ;;
        --username)       [ $# -ge 2 ] || { usage; exit 2; }; username="$2"; shift 2 ;;
        --recreate)         recreate=1; shift ;;
        --checked)          checked=1; shift ;;
        --no-health-check)  skip_health_check=1; shift ;;
        --yes)            confirmed=1; shift ;;
        -h|--help)        usage; exit 0 ;;
        *)                printf 'Unknown option: %s\n\n' "$1" >&2; usage; exit 2 ;;
    esac
done

[ -n "$manifest" ] || { usage; exit 2; }
[ -f "$manifest" ] || fail "No manifest at '$manifest'."

command -v pg_restore >/dev/null 2>&1 \
    || fail "pg_restore is not on PATH. Install postgresql-client of the SERVER's major version."
command -v psql >/dev/null 2>&1 \
    || fail "psql is not on PATH. It comes with the same postgresql-client package as pg_restore."

# ---------------------------------------------------------------------------
# Read the manifest. Its shape is fixed and indented, so these patterns are exact rather than a
# general JSON parser; anything that does not match is reported instead of guessed at.
#
# The file is read once, with its own status checked, and the patterns below run over that text. Read
# straight from the file inside `$(sed ... "$manifest" | head -n 1)` the read's failure would be
# reported as head's success, and an unreadable manifest would come back as a set of empty fields.
# ---------------------------------------------------------------------------
manifest_text=$(cat "$manifest") \
    || fail "REFUSED: the manifest at '$manifest' exists but could not be read."
[ -n "$manifest_text" ] || fail "REFUSED: the manifest at '$manifest' is empty."

field() {
    printf '%s\n' "$manifest_text" \
        | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
        | head -n 1
}

format_version=$(printf '%s\n' "$manifest_text" \
    | sed -n 's/.*"formatVersion"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' | head -n 1)
created_at=$(field createdAt)
dump_file=$(field dumpFileName)
dump_sha=$(printf '%s\n' "$manifest_text" \
    | sed -n 's/.*"dumpSha256"[[:space:]]*:[[:space:]]*"\([0-9a-f]\{64\}\)".*/\1/p' | head -n 1)
source_database=$(field databaseName)
server_version=$(field serverVersion)

[ "$format_version" = "1" ] || fail "Manifest format version '$format_version' is not one this script reads (1)."
[ -n "$dump_sha" ] || fail "The manifest carries no dumpSha256; it is not a Cinomni backup manifest."

[ -n "$dump" ] || dump="$(dirname "$manifest")/$dump_file"
[ -f "$dump" ] || fail "No archive at '$dump'."
[ -n "$database" ] || database="$source_database"
[ -n "$database" ] || fail "The manifest names no database; pass --database."

# The name is put into SQL below (there is no other way to say DROP DATABASE), so it is restricted to
# an unquoted identifier here rather than escaped later.
printf '%s' "$database" | grep -Eq '^[A-Za-z_][A-Za-z0-9_]{0,62}$' \
    || fail "Database name '$database' is not a plain identifier (letters, digits and _, up to 63)."
printf '%s' "$maintenance_db" | grep -Eq '^[A-Za-z_][A-Za-z0-9_]{0,62}$' \
    || fail "Maintenance database name '$maintenance_db' is not a plain identifier."

# A database cannot be dropped from a session connected to it. Said here rather than left to the
# server, because --maintenance-db is exactly what an operator reaches for when the role cannot
# connect to `postgres`, and the target is the obvious wrong thing to name.
if [ "$recreate" -eq 1 ] && [ "$maintenance_db" = "$database" ]; then
    fail "REFUSED: --recreate drops '$database', so it cannot also be the maintenance database.
Pass --maintenance-db naming another database this role may connect to."
fi

# ---------------------------------------------------------------------------
# Integrity. A dump copied off the machine is the one that arrives truncated.
#
# It is integrity and not authenticity: the expected hash is in the unsigned manifest beside the
# archive, so this catches a bad copy, never a substituted one. See the header.
#
# The hashing tool's own exit status is checked. Piping it into cut would report cut's status, so an
# unreadable file would yield an empty string and slip through the comparison below unverified.
# ---------------------------------------------------------------------------
actual=''
if command -v sha256sum >/dev/null 2>&1; then
    hashed=$(sha256sum "$dump") || fail "REFUSED: sha256sum could not read '$dump'."
    actual=${hashed%% *}
elif command -v shasum >/dev/null 2>&1; then
    hashed=$(shasum -a 256 "$dump") || fail "REFUSED: shasum could not read '$dump'."
    actual=${hashed%% *}
else
    printf 'WARNING: no sha256sum or shasum on PATH; the archive was NOT verified.\n' >&2
fi

if command -v sha256sum >/dev/null 2>&1 || command -v shasum >/dev/null 2>&1; then
    [ -n "$actual" ] || fail "REFUSED: hashing '$dump' produced no digest, so it could not be verified."
fi

if [ -n "$actual" ] && [ "$actual" != "$dump_sha" ]; then
    fail "REFUSED: '$dump' hashes to $actual and the manifest records $dump_sha. Do not restore it."
fi

# ---------------------------------------------------------------------------
# One place to ask the server a question. The guard below and the empty-target check further down both
# need it, and neither may put a password on a command line.
#
# Callers check the status of this and only then look at what it printed. It is never the left side of
# a pipe: that would report the filter's status and turn a failed query into an empty answer.
# ---------------------------------------------------------------------------
maintenance() {
    psql --no-password --quiet --no-align --tuples-only \
        --host "$host" --port "$port" --username "$username" --dbname "$1" --command "$2"
}

# How many clients are attached to the database that is about to be replaced. The name has already
# been restricted to a plain identifier above, so it cannot escape this literal. Our own session is
# excluded; every other one is somebody driving the installation.
#
# Asked from the maintenance database, which is why this role needs CONNECT there on every run: the
# target may be dropped a moment later, and on a restore into a new database it does not exist yet.
# pg_stat_activity is cluster-wide, so any database the role can reach answers this.
attached_sessions() {
    maintenance "$maintenance_db" \
        "SELECT count(*) FROM pg_stat_activity
         WHERE datname = '$database' AND pid <> pg_backend_pid();"
}

# ---------------------------------------------------------------------------
# The application must be stopped. A restore under a running Host is a corrupted installation: its
# outbox relay, command worker and scheduler would be driving a database being replaced underneath
# them. So this guard fails CLOSED, and it has to do so in both topologies this script runs in:
#
#   - On the host, the default URL is the published one, built from the same variables .env uses, so
#     an installation on a non-default port is probed rather than silently skipped.
#   - Inside a container — which is where pg_restore lives in the packaged deployment, because the
#     database publishes no host port — that same loopback is the throwaway container's own, where
#     nothing ever listens. "Could not connect" there means "could not tell", NOT "nothing is
#     running", and treating the two as the same is exactly the conflation this guard exists to
#     avoid. DEPLOYMENT.md's containerised procedure sets CINOMNI_HEALTH_URL to the service name.
#
# So: any HTTP answer refuses, whatever its status — a Host reporting 503 because a dependency is
# down is still a Host with workers running. A probe that reaches nothing is inconclusive and falls
# back to the one observation that is conclusive in every topology: whether any client session is
# attached to the database being replaced. That question can always be asked, since the server has to
# be reachable for the restore itself, and a live Host always holds a connection because its relay
# and scheduler poll continuously. If neither question can be answered, this refuses.
# ---------------------------------------------------------------------------
health_host="${CINOMNI_BIND_ADDRESS:-127.0.0.1}"
case "$health_host" in
    0.0.0.0|::|'[::]') health_host='127.0.0.1' ;;
esac
health_url="${CINOMNI_HEALTH_URL-http://$health_host:${CINOMNI_HTTP_PORT:-8080}/health}"

if [ "$skip_health_check" -eq 1 ]; then
    printf 'Not probing for a running application (--no-health-check).\n' >&2
else
    [ -n "$health_url" ] || fail "REFUSED: no health URL to probe. Set CINOMNI_HEALTH_URL to where this
installation answers, or pass --no-health-check if you have confirmed it is stopped."

    command -v curl >/dev/null 2>&1 || fail "REFUSED: curl is not on PATH, so this cannot check whether
Cinomni is still running. Install curl, or pass --no-health-check if you have confirmed it is stopped."

    # No --fail: any answer at all means something is listening there, including an unhealthy one.
    if curl --silent --max-time 3 --output /dev/null "$health_url" 2>/dev/null; then
        fail "REFUSED: something is answering $health_url, so Cinomni looks like it is still running.
Stop it first (docker compose stop cinomni). Pass --no-health-check if that is not Cinomni."
    fi

    printf 'Nothing answered %s, which is not proof that nothing is running. Asking the database.\n' \
        "$health_url" >&2

    sessions=$(attached_sessions) || fail "REFUSED: $health_url could not be reached AND the database
could not be asked whether anything is still attached to '$database'. Neither question was answered,
so this will not restore.
That question is asked over a connection to '$maintenance_db', which every run needs whether or not
--recreate was passed. If this role has no CONNECT there, pass --maintenance-db naming a database it
can connect to; any of them answers it. Otherwise fix the connection, set CINOMNI_HEALTH_URL to where
this installation answers, or pass --no-health-check once you have confirmed the application is
stopped."

    sessions=$(printf '%s' "$sessions" | tr -d '[:space:]')
    printf '%s' "$sessions" | grep -Eq '^[0-9]+$' || fail "REFUSED: the session count for '$database'
came back as '$sessions', which is not a number, so it could not be read. Pass --no-health-check once
you have confirmed the application is stopped."

    if [ "$sessions" -ne 0 ]; then
        fail "REFUSED: $sessions client session(s) are still attached to '$database'. That is what a
running Cinomni looks like from here, whatever the health probe could reach. Stop it first
(docker compose stop cinomni). Pass --no-health-check only if you know those sessions are not Cinomni."
    fi

    printf 'No client session is attached to %s, so it is treated as stopped.\n' "$database" >&2
fi

# ---------------------------------------------------------------------------
# Say what this will do, then require it to have been said out loud.
# ---------------------------------------------------------------------------
schema_count=$(printf '%s\n' "$manifest_text" | grep -c '"name"[[:space:]]*:' || true)
target_note='  (must already be empty)'
[ "$recreate" -eq 0 ] || target_note='  (will be DROPPED and recreated - everything in it is destroyed)'

cat <<SUMMARY
Backup
  archive        $dump
  taken          $created_at
  from database  $source_database on PostgreSQL $server_version
  sha256         ${actual:-not verified}
  schemas        ${schema_count:-?}

Restore target
  $username@$host:$port/$database
 $target_note
SUMMARY

if [ "$checked" -eq 0 ]; then
    cat >&2 <<'CHECK'

REFUSED: run the compatibility check first, then pass --checked.

    dotnet Cinomni.Host.dll backup check <backup-name>

It refuses a dump the build you are about to run cannot read — one taken by a NEWER version, whose
tables this code has no model for and no migration path back to. Nothing in this script can answer
that question: it does not know which build you will start afterwards.
CHECK
    exit 1
fi

if [ "$confirmed" -eq 0 ]; then
    printf '\nNothing was changed. Re-run with --yes to restore.\n'
    exit 1
fi

# ---------------------------------------------------------------------------
# Recreate, or prove the target is empty. Restoring on top of an existing installation is not
# offered: pg_restore --clean cannot drop a partition's inherited primary key, so that path dies
# half-way through with the database in neither state.
# ---------------------------------------------------------------------------
if [ "$recreate" -eq 1 ]; then
    printf '\nDropping and recreating %s...\n' "$database"
    # FORCE terminates any session still attached; the application is already stopped by this point.
    maintenance "$maintenance_db" "DROP DATABASE IF EXISTS $database WITH (FORCE);" >/dev/null
    maintenance "$maintenance_db" "CREATE DATABASE $database;" >/dev/null
else
    # The query runs on its own and its status is checked before its output is read. Piped straight
    # into tr this reported tr's status, so a query that could not run came back as an empty string
    # that `${existing:-0}` then read as "no schemas" — the one guard standing between a restore and a
    # populated installation, failing open exactly when the server could not be asked.
    existing=$(maintenance "$database" \
        "SELECT count(*) FROM information_schema.schemata
         WHERE schema_name NOT IN ('public', 'information_schema')
           AND schema_name NOT LIKE 'pg\\_%';") || fail "REFUSED: '$database' could not be inspected,
so whether it is empty is unknown, and a restore into an installation that is already there is not
something to guess at. It may not exist yet — create it, or re-run with --recreate — or this role may
not be able to connect to it."

    existing=$(printf '%s' "$existing" | tr -d '[:space:]')
    printf '%s' "$existing" | grep -Eq '^[0-9]+$' || fail "REFUSED: the schema count for '$database'
came back as '$existing', which is not a number, so whether it is empty could not be established."

    if [ "$existing" != "0" ]; then
        fail "REFUSED: '$database' already holds $existing schema(s).
A restore must go into an empty database. Either:
  - re-run with --recreate, which drops and recreates it; or
  - create a new one, restore into that, and point ConnectionStrings__Cinomni at it."
    fi
fi

# ---------------------------------------------------------------------------
# Restore. One transaction: it lands completely or the database is left exactly as it was.
# Ownership and privileges are dropped from the archive because the restoring role need not be the
# dumping one on a single-owner node.
# ---------------------------------------------------------------------------
printf '\nRestoring...\n'
pg_restore \
    --no-owner \
    --no-privileges \
    --exit-on-error \
    --single-transaction \
    --no-password \
    --host "$host" \
    --port "$port" \
    --username "$username" \
    --dbname "$database" \
    "$dump"

cat <<DONE

Restored into $database.

Next:
  1. Point ConnectionStrings__Cinomni at '$database' if that is not already where it points, and
     start Cinomni. It applies any migrations the dump predates before it serves anything.
  2. Confirm /health answers 200.
  3. If this dump is old, sign every account out: it restored the session tokens that were valid
     when it was taken, including ones revoked afterwards.
  4. Downloads resume from the checkpoints in the dump. Anything acquired after it was taken is
     unknown to the installation again — the files are still on disk, and re-importing them is the
     way back.
DONE
