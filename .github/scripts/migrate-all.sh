#!/usr/bin/env bash
#
# Migration gate.
#
# The Host applies every module's pending migrations on startup, so a missing or broken migration
# only ever surfaced when somebody ran the Host. This script drives the same set of schemas from
# the command line so CI can prove three things the repository never proved before:
#
#   update       every schema applies to the database named by CINOMNI_DB. Run it a second time and
#                nothing must be applied: that is the idempotence the restart story depends on.
#   check-drift  no model was changed without generating the matching migration.
#
# The project list is discovered, not hardcoded (see ef-projects.sh): an EF project is a project
# directory that owns a Persistence/Migrations folder, cross-checked against the design-time
# DbContext factories so a project whose migrations landed elsewhere fails the gate instead of
# disappearing from it. A new module enters this gate automatically, and a module with no schema of
# its own (RealTime) is correctly absent.
#
# CINOMNI_DB is the only environment variable the design-time factories read; leave it unset to use
# the local development default from docker-compose.dev.yml.
#
# Usage: bash .github/scripts/migrate-all.sh <update|check-drift> [repository-root]
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
verb="${1:-}"
root="${2:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

# shellcheck source=.github/scripts/ef-projects.sh
. "$script_dir/ef-projects.sh"

if [ "$verb" != "update" ] && [ "$verb" != "check-drift" ]; then
  printf 'Usage: %s <update|check-drift> [repository-root]\n' "$0" >&2
  exit 2
fi

# Set CINOMNI_EF_NO_BUILD=1 after a solution build to skip 16 redundant project builds.
ef_extra=()
if [ "${CINOMNI_EF_NO_BUILD:-0}" = "1" ]; then
  ef_extra=(--no-build --configuration "${CINOMNI_BUILD_CONFIGURATION:-Release}")
fi

if ! projects="$(cinomni_ef_projects)"; then
  printf 'EF project discovery is inconsistent (see above), so this gate cannot claim full coverage.\n' >&2
  exit 1
fi

if [ -z "$projects" ]; then
  printf 'No EF projects found under src/. Expected at least the Operations platform schema.\n' >&2
  exit 1
fi

count="$(printf '%s\n' "$projects" | wc -l | tr -d ' ')"
printf 'Running "%s" over %s EF project(s).\n\n' "$verb" "$count"

failed=0
while IFS= read -r project; do
  [ -n "$project" ] || continue
  printf '=== %s ===\n' "$project"

  case "$verb" in
    update)
      if ! dotnet ef database update \
        --project "$project" --startup-project "$project" "${ef_extra[@]}"; then
        printf 'FAILED to apply migrations for %s\n\n' "$project" >&2
        failed=$((failed + 1))
      fi
      ;;
    check-drift)
      # Exits non-zero when the model has changes with no migration behind them.
      if ! dotnet ef migrations has-pending-model-changes \
        --project "$project" --startup-project "$project" "${ef_extra[@]}"; then
        printf 'MODEL DRIFT in %s: the model changed without a migration. Generate one (CONTRIBUTING.md §7).\n\n' \
          "$project" >&2
        failed=$((failed + 1))
      fi
      ;;
  esac
  printf '\n'
done <<EOF
$projects
EOF

if [ "$failed" -gt 0 ]; then
  printf '%s failed for %d project(s).\n' "$verb" "$failed" >&2
  exit 1
fi

printf '%s succeeded for all %s EF project(s).\n' "$verb" "$count"
