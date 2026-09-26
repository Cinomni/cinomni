#!/usr/bin/env bash
#
# Upgrade gate: this change's migrations applied on top of an existing installation.
#
# "Every migration applies to an empty database" is not the property a self-hosted product needs.
# Every user upgrades a database that already holds the previous schema and their data, so the path
# that actually runs on their machine is "previous schema, then the new migrations only". A column
# added NOT NULL with no default, or a migration edited after release, passes an from-empty check
# and breaks that upgrade.
#
# This script drives exactly that path against the database named by CINOMNI_DB:
#
#   1. It reads, from <base-ref>, the migrations every existing installation has already applied.
#   2. It asserts those files are byte-identical at HEAD. An applied migration is history: changing
#      one changes nothing on an installation that already ran it, so the database and the code
#      diverge silently (CONTRIBUTING.md §7). Steps 3 and 4 also depend on it,
#      because they replay the base state using the code in this tree.
#   3. It brings every EF project to its <base-ref> migration — the installed schema.
#   4. It applies the remaining migrations, which is the upgrade the user will run.
#
# The database must be empty when this starts: step 3 builds the "already installed" state itself.
#
# Usage: bash .github/scripts/migration-upgrade.sh <base-ref> [repository-root]
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
base_ref="${1:-}"
root="${2:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

# shellcheck source=.github/scripts/ef-projects.sh
. "$script_dir/ef-projects.sh"

if [ -z "$base_ref" ]; then
  printf 'Usage: %s <base-ref> [repository-root]\n' "$0" >&2
  exit 2
fi

# A push event reports an all-zero "before" for a new branch, and workflow_dispatch reports nothing.
# Fall back to the first parent, which is still a real previous state; never skip the gate silently.
resolve_base() {
  candidate="$1"
  case "$candidate" in
    ''|0000000000000000000000000000000000000000) candidate='HEAD^' ;;
  esac

  git rev-parse --verify --quiet "${candidate}^{commit}"
}

# The repository's first commit has no parent, so nothing was installed before it: there is no
# earlier schema to protect or to upgrade from.
case "$base_ref" in
  ''|0000000000000000000000000000000000000000)
    if ! git rev-parse --verify --quiet 'HEAD^' >/dev/null; then
      printf 'No base commit: %s is the first commit, so there is no installed schema to check against.
' "$(git rev-parse --short HEAD)"
      exit 0
    fi
    ;;
esac

if ! base="$(resolve_base "$base_ref")"; then
  printf 'Cannot resolve a base commit from "%s".\n' "$base_ref" >&2
  printf 'Check out enough history (fetch-depth: 0) and pass the commit the change is based on.\n' >&2
  exit 1
fi

printf 'Upgrading from base %s (%s)\n\n' "$(git rev-parse --short "$base")" "$base_ref"

# Set CINOMNI_EF_NO_BUILD=1 after a solution build to skip redundant project builds.
ef_extra=()
if [ "${CINOMNI_EF_NO_BUILD:-0}" = "1" ]; then
  ef_extra=(--no-build --configuration "${CINOMNI_BUILD_CONFIGURATION:-Release}")
fi

if ! projects="$(cinomni_ef_projects)"; then
  printf 'EF project discovery is inconsistent (see above), so this gate cannot claim full coverage.\n' >&2
  exit 1
fi

# The migration files a project had at the base commit, newest last. Designer files and the model
# snapshot are excluded: only the migration classes carry applied history.
base_migration_files() {
  git ls-tree -r --name-only "$base" -- "$1/Persistence/Migrations" |
    grep -E '/[0-9]{14}_[^/]*\.cs$' |
    grep -v '\.Designer\.cs$' |
    LC_ALL=C sort || true
}

failed=0
declare -a upgraded_projects=()

printf '=== Step 1: applied migrations must not have changed since %s ===\n' "$(git rev-parse --short "$base")"
while IFS= read -r project; do
  [ -n "$project" ] || continue
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    # Compare the base against the working tree, so a local run is as honest as a CI run.
    if ! git diff --quiet "$base" -- "$file" 2>/dev/null; then
      printf 'APPLIED MIGRATION CHANGED: %s\n' "$file" >&2
      printf '  Every existing installation already ran this migration, so editing or deleting it\n' >&2
      printf '  leaves their database and this code permanently out of step. Add a corrective\n' >&2
      printf '  migration instead (CONTRIBUTING.md §7).\n\n' >&2
      failed=$((failed + 1))
    fi
  done <<EOF
$(base_migration_files "$project")
EOF
done <<EOF
$projects
EOF

if [ "$failed" -gt 0 ]; then
  printf '%d applied migration(s) were modified. Not replaying an upgrade on a rewritten history.\n' "$failed" >&2
  exit 1
fi
printf 'No applied migration was modified.\n\n'

printf '=== Step 2: build the schema an existing installation already has ===\n'
while IFS= read -r project; do
  [ -n "$project" ] || continue

  last_base="$(base_migration_files "$project" | tail -n 1)"
  if [ -z "$last_base" ]; then
    printf '%s: new EF project, nothing installed at the base commit.\n' "$project"
    continue
  fi

  target="$(basename "$last_base" .cs)"
  printf '%s: installing up to %s\n' "$project" "$target"
  if ! dotnet ef database update "$target" \
    --project "$project" --startup-project "$project" "${ef_extra[@]}"; then
    printf 'FAILED to reach the base schema for %s\n\n' "$project" >&2
    failed=$((failed + 1))
  fi

  head_last="$(
    find "$project/Persistence/Migrations" -maxdepth 1 -name '*.cs' -not -name '*.Designer.cs' |
      sed 's|.*/||' |
      grep -E '^[0-9]{14}_' |
      LC_ALL=C sort | tail -n 1
  )"
  if [ -n "$head_last" ] && [ "$(basename "$head_last" .cs)" != "$target" ]; then
    upgraded_projects+=("$project")
  fi
done <<EOF
$projects
EOF

if [ "$failed" -gt 0 ]; then
  printf 'Could not reproduce the installed schema for %d project(s).\n' "$failed" >&2
  exit 1
fi

if [ "${#upgraded_projects[@]}" -eq 0 ]; then
  printf '\nNo project adds a migration in this change; the upgrade is a no-op and must stay one.\n'
else
  printf '\nProjects with new migrations: %s\n' "${upgraded_projects[*]}"
fi

printf '\n=== Step 3: apply this change on top of it ===\n'
while IFS= read -r project; do
  [ -n "$project" ] || continue
  printf '=== %s ===\n' "$project"
  if ! dotnet ef database update \
    --project "$project" --startup-project "$project" "${ef_extra[@]}"; then
    printf 'FAILED to upgrade %s from its installed schema.\n\n' "$project" >&2
    failed=$((failed + 1))
  fi
  printf '\n'
done <<EOF
$projects
EOF

if [ "$failed" -gt 0 ]; then
  printf 'The upgrade failed for %d project(s). An existing installation would fail the same way at startup.\n' "$failed" >&2
  exit 1
fi

printf 'Upgrade OK: every migration applied on top of the schema installed at %s.\n' \
  "$(git rev-parse --short "$base")"
