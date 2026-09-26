#!/usr/bin/env bash
#
# Migration safety gate: no silent data loss and no upgrade that cannot succeed.
#
# The Host applies pending migrations at startup on the user's own database
# (src/Host/Cinomni.Host/Program.cs), so a migration is a production operation on data nobody can
# get back. An empty CI database cannot show that: dropping a column that holds rows and adding a
# NOT NULL column to a table that holds rows both succeed against nothing.
#
# This reads every migration this change adds or edits and fails on the two operations that are
# harmless when the table is empty and destructive when it is not:
#
#   - DropTable / DropColumn / DropSchema        - existing rows are gone, unrecoverably.
#   - AddColumn / AlterColumn to NOT NULL with   - the upgrade aborts on an installed database and
#     no defaultValue or defaultValueSql           the Host never finishes starting.
#
# Only the Up method is read: EF generates a Drop for every Create in Down, and Down is a rollback,
# not an upgrade. Renames are deliberately not flagged; the MVP is a single node that stops, then
# migrates, then starts, so a rename is not a compatibility break the way a drop is.
#
# CONTRIBUTING.md §7 prefers expand/contract for incompatible changes. When a
# destructive step is the deliberate decision, mark that migration file with a line containing
#
#   cinomni:destructive-migration <why this is safe, and what protects the data>
#
# which puts the decision in the diff a reviewer reads instead of in a pull request comment.
#
# Usage: bash .github/scripts/check-migration-safety.sh <base-ref> [repository-root]
set -euo pipefail

base_ref="${1:-}"
root="${2:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

if [ -z "$base_ref" ]; then
  printf 'Usage: %s <base-ref> [repository-root]\n' "$0" >&2
  exit 2
fi

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

marker='cinomni:destructive-migration'

changed="$(
  git diff --name-only --diff-filter=AM "$base" -- src |
    grep -E '/Persistence/Migrations/[0-9]{14}_[^/]*\.cs$' |
    grep -v '\.Designer\.cs$' |
    LC_ALL=C sort || true
)"

if [ -z "$changed" ]; then
  printf 'No migration added or changed since %s.\n' "$(git rev-parse --short "$base")"
  exit 0
fi

printf 'Migrations added or changed since %s:\n%s\n\n' "$(git rev-parse --short "$base")" "$changed"

failed=0

# The Up body of a migration, as one statement per line, so a call that EF wrapped across a dozen
# lines can be matched as a whole.
up_statements() {
  awk '
    /protected override void Up\(/ { inside = 1 }
    /protected override void Down\(/ { inside = 0 }
    inside
  ' "$1" | tr '\n' ' ' | tr ';' '\n'
}

report() {
  printf 'UNSAFE MIGRATION\n  file: %s\n  operation: %s\n  reason: %s\n\n' "$1" "$2" "$3" >&2
  failed=$((failed + 1))
}

while IFS= read -r file; do
  [ -n "$file" ] || continue
  [ -f "$file" ] || continue

  if grep -qF "$marker" "$file"; then
    printf 'ACCEPTED (explicitly marked): %s\n' "$file"
    grep -F "$marker" "$file" | sed 's/^[[:space:]]*/  /'
    printf '\n'
    continue
  fi

  statements="$(up_statements "$file")"

  while IFS= read -r statement; do
    case "$statement" in
      *migrationBuilder.DropTable*)
        report "$file" "DropTable" "every row in that table is lost on an installed database" ;;
      *migrationBuilder.DropColumn*)
        report "$file" "DropColumn" "every value in that column is lost on an installed database" ;;
      *migrationBuilder.DropSchema*)
        report "$file" "DropSchema" "the whole module schema is lost on an installed database" ;;
    esac

    # A NOT NULL column with no default fails on rows that already exist. AlterColumn only counts
    # when the column was nullable before ("oldNullable: true"); EF omits that argument otherwise,
    # and re-stating an existing NOT NULL adds no constraint.
    unsafe_not_null=''
    case "$statement" in
      *migrationBuilder.AddColumn*'nullable: false'*) unsafe_not_null='AddColumn' ;;
      *migrationBuilder.AlterColumn*'nullable: false'*)
        case "$statement" in
          *'oldNullable: true'*) unsafe_not_null='AlterColumn' ;;
        esac
        ;;
    esac

    case "$statement" in
      *defaultValue*) unsafe_not_null='' ;;
    esac

    if [ -n "$unsafe_not_null" ]; then
      report "$file" "$unsafe_not_null to NOT NULL without a default" \
        "the upgrade fails on any table that already holds rows, so the Host never finishes starting"
    fi
  done <<EOF
$statements
EOF
done <<EOF
$changed
EOF

if [ "$failed" -gt 0 ]; then
  printf 'Migration safety check failed: %d unsafe operation(s).\n' "$failed" >&2
  printf 'Use expand/contract, or mark the file with "%s <reason>" if the loss is the decision.\n' \
    "$marker" >&2
  exit 1
fi

printf 'Migration safety OK: nothing added drops data or adds a NOT NULL column without a default.\n'
