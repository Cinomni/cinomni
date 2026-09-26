#!/usr/bin/env bash
#
# Host startup smoke.
#
# Starts the real composition root against the given database, waits until this run's own log says
# it is listening and /health reports healthy, stops it, and waits for the port to be free again.
# Running it twice in a row against the same database is the point: the second run proves the
# Migrate*Async calls and RecoverOperationsAsync are re-entrant, which is the restart-survival
# property the whole recovery story rests on.
#
# Three properties are what make the second run mean anything:
#
#   1. The listen port must be free before starting. A Host left over from the previous run would
#      otherwise answer /health in milliseconds and the gate would pass having started nothing —
#      and the run that was supposed to prove re-entrancy would have died unable to bind.
#   2. The built assembly is launched directly, so the recorded pid IS the application. "dotnet run"
#      is a launcher that runs the application as a separate child; killing the launcher does not
#      reliably stop it, which is how an orphan survives into the next run in the first place.
#      Launching the assembly also means there are no grandchildren to chase.
#   3. Success needs "Now listening on: <url>" from this run's log as well as a healthy /health, so
#      the evidence comes from the process this script started and not from whatever holds the port.
#
# Stopping is bounded: SIGTERM, then SIGKILL if the Host is still alive, then a wait for the port to
# stop answering. A Host that cannot be stopped fails the run instead of hanging the job.
#
# The Host runs in Production, which is what makes this gate worth having and also what makes the
# storage roots this script's business. Production is the only environment where the packaging
# contract is checked, and the packaged defaults are container mount points (/data/library,
# /data/downloads, /data/transcodes) that no ordinary account on a runner or a developer machine may
# write. Left alone they fail the run twice over: the two import roots are fatal, so the Host refuses
# to start at all, and the transcode root — only a warning — then stays absent, which the readiness
# probe reports as Unhealthy, a 503 and not a pass. So all three are pointed at a directory this
# account owns.
#
# One directory with three siblings, not three unrelated temporary directories: a hardlink from the
# staging path into the library root is probed at boot, and two roots on two filesystems would emit a
# warning about the machine that ran the gate rather than about the change under test.
#
# The location is stable for the whole job rather than a fresh `mktemp -d` per invocation, because the
# second run has to be the same installation as the first — that is the restart this proves — and it
# lives outside the repository so a run leaves nothing for git to see. It is deliberately NOT deleted
# on exit: the runner discards RUNNER_TEMP with the job, an empty tree costs a contributor nothing,
# and CINOMNI_SMOKE_STORAGE may point anywhere — a cleanup step here would be one exported variable
# away from deleting somebody's media root.
#
# The connection string is assembled from arguments and the development defaults; nothing secret is
# involved and nothing is echoed. /health is anonymous by design, so no credential is needed.
#
# Usage: bash .github/scripts/host-smoke.sh <database> [repository-root]
set -euo pipefail

database="${1:-}"
root="${2:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

if [ -z "$database" ]; then
  printf 'Usage: %s <database> [repository-root]\n' "$0" >&2
  exit 2
fi

host="${CINOMNI_DB_HOST:-localhost}"
port="${CINOMNI_DB_PORT:-5442}"
user="${CINOMNI_DB_USER:-cinomni}"
password="${CINOMNI_DB_PASSWORD:-cinomni_dev}"
listen_url="${CINOMNI_SMOKE_URL:-http://127.0.0.1:5268}"
attempts="${CINOMNI_SMOKE_ATTEMPTS:-60}"
stop_attempts="${CINOMNI_SMOKE_STOP_ATTEMPTS:-30}"
log_file="${CINOMNI_SMOKE_LOG:-host-smoke.log}"
configuration="${CINOMNI_BUILD_CONFIGURATION:-Release}"
storage_root="${CINOMNI_SMOKE_STORAGE:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/cinomni-host-smoke}"

# The port has to be known explicitly, because the whole gate rests on proving it free.
listen_host="$(printf '%s' "$listen_url" | sed 's|^[A-Za-z][A-Za-z0-9+.-]*://||; s|/.*$||; s|:[0-9]*$||')"
listen_port="$(printf '%s' "$listen_url" | sed 's|/*$||; s|.*:||')"
case "$listen_port" in
  ''|*[!0-9]*)
    printf 'CINOMNI_SMOKE_URL must carry an explicit port (got "%s").\n' "$listen_url" >&2
    exit 2
    ;;
esac

# True when anything accepts a connection on the listen port, whether or not it is our Host.
port_in_use() {
  if (exec 3<>"/dev/tcp/$listen_host/$listen_port") 2>/dev/null; then
    return 0
  fi
  curl -fsS --max-time 2 "$listen_url/health" >/dev/null 2>&1
}

if port_in_use; then
  printf 'Something is already listening on %s, so this run could not prove anything.\n' "$listen_url" >&2
  printf 'A Host from a previous run may have survived; stop it, or set CINOMNI_SMOKE_URL to a free port.\n' >&2
  exit 1
fi

assembly="src/Host/Cinomni.Host/bin/$configuration/net10.0/Cinomni.Host.dll"
if [ ! -f "$assembly" ]; then
  printf 'Building the Host in %s, because %s does not exist yet.\n' "$configuration" "$assembly"
  dotnet build src/Host/Cinomni.Host --configuration "$configuration"
fi
if [ ! -f "$assembly" ]; then
  printf 'Expected the Host assembly at %s and it is not there.\n' "$assembly" >&2
  exit 1
fi

host_pid=""

stop_host() {
  [ -n "$host_pid" ] || return 0

  if kill -0 "$host_pid" 2>/dev/null; then
    kill -TERM "$host_pid" 2>/dev/null || true

    waited=0
    while [ "$waited" -lt "$stop_attempts" ] && kill -0 "$host_pid" 2>/dev/null; do
      sleep 1
      waited=$((waited + 1))
    done

    if kill -0 "$host_pid" 2>/dev/null; then
      printf 'The Host ignored SIGTERM for %ds; sending SIGKILL.\n' "$waited" >&2
      kill -KILL "$host_pid" 2>/dev/null || true
    fi
  fi

  wait "$host_pid" 2>/dev/null || true
  host_pid=""
}

# The Host never exits on its own, so it must be stopped on every exit path, including a failed
# health poll and a job cancellation — and the port must be free afterwards, or the next run in the
# same job would be measuring this one.
# shellcheck disable=SC2329 # Invoked indirectly by trap.
cleanup() {
  status=$?
  trap - EXIT INT TERM

  stop_host

  waited=0
  while [ "$waited" -lt "$stop_attempts" ] && port_in_use; do
    sleep 1
    waited=$((waited + 1))
  done

  if port_in_use; then
    printf 'Something is still listening on %s after the Host was stopped.\n' "$listen_url" >&2
    printf 'A later run against this port would report on that process instead of its own.\n' >&2
    status=1
  fi

  exit "$status"
}
trap cleanup EXIT INT TERM

printf 'Starting %s against database "%s" on %s\n' "$assembly" "$database" "$listen_url"
printf 'Storage roots under %s (not removed on exit — see the header).\n' "$storage_root"

# The three roots override the packaged defaults, in the double-underscore form the environment
# configuration provider reads. The Host creates each root it does not find, which is the packaged
# behaviour rather than a concession to this script, so nothing is made here.
ConnectionStrings__Cinomni="Host=$host;Port=$port;Database=$database;Username=$user;Password=$password" \
  Import__LibraryRoot="$storage_root/library" \
  Downloads__Sidecar__StagingPath="$storage_root/downloads" \
  Playback__TranscodeRoot="$storage_root/transcodes" \
  ASPNETCORE_URLS="$listen_url" \
  ASPNETCORE_ENVIRONMENT=Production \
  dotnet "$assembly" >"$log_file" 2>&1 &
host_pid=$!

listening_marker="Now listening on: $listen_url"
saw_listening=0
attempt=0

while [ "$attempt" -lt "$attempts" ]; do
  attempt=$((attempt + 1))

  if ! kill -0 "$host_pid" 2>/dev/null; then
    printf 'The Host exited before answering /health. Output:\n' >&2
    cat "$log_file" >&2
    exit 1
  fi

  if [ "$saw_listening" -eq 0 ] && grep -qF "$listening_marker" "$log_file"; then
    saw_listening=1
    printf 'The Host this run started is listening on %s.\n' "$listen_url"
  fi

  if [ "$saw_listening" -eq 1 ] && body="$(curl -fsS --max-time 5 "$listen_url/health" 2>/dev/null)"; then
    printf '/health answered after %d attempt(s): %s\n' "$attempt" "$body"
    # /health is the readiness probe, and it reports per-check statuses. Degraded is a pass here on
    # purpose: this job runs no torrent sidecar, so the sidecar check is down by construction, and a
    # gate that demanded Healthy would be demanding a dependency this gate does not start. Unhealthy
    # is a 503, which curl --fail has already rejected above; the match is the second line of defence.
    case "$body" in
      *'"status":"Healthy"'*|*'"status":"Degraded"'*)
        printf 'Host startup smoke OK.\n'
        exit 0
        ;;
      *)
        printf '/health did not report a servable status.\n' >&2
        cat "$log_file" >&2
        exit 1
        ;;
    esac
  fi

  sleep 2
done

if [ "$saw_listening" -eq 0 ]; then
  printf 'The Host never logged "%s" within %d attempts. Output:\n' "$listening_marker" "$attempts" >&2
else
  printf 'The Host listened but did not answer %s/health within %d attempts. Output:\n' \
    "$listen_url" "$attempts" >&2
fi
cat "$log_file" >&2
exit 1
