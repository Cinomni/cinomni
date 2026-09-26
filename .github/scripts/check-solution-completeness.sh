#!/usr/bin/env bash
#
# Solution completeness gate.
#
# CI builds and tests src/Cinomni.slnx. Adding a project to the solution is a manual step
# (CONTRIBUTING.md §6.7), so forgetting it silently removes a whole module — or a whole test
# assembly — from every gate without anything turning red. This script fails when a project file
# exists on disk but is absent from the solution.
#
# Usage: bash .github/scripts/check-solution-completeness.sh [repository-root]
set -euo pipefail

root="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

solution="src/Cinomni.slnx"
missing=0

if [ ! -f "$solution" ]; then
  printf 'Solution file not found: %s\n' "$solution" >&2
  exit 1
fi

# The solution stores paths relative to src/ and with forward slashes; compare on the file name
# plus its parent directory, which is unique across the repository and immune to that difference.
# shellcheck disable=SC1003 # '\\' is a literal backslash for tr, not an escaped quote.
listed="$(tr -d '\r' <"$solution" | grep -o 'Path="[^"]*"' | sed 's/Path="//; s/"$//' | tr '\\' '/')"

while IFS= read -r project; do
  [ -n "$project" ] || continue
  key="$(basename "$(dirname "$project")")/$(basename "$project")"
  if ! grep -qF -- "$key" <<<"$listed"; then
    printf 'MISSING FROM SOLUTION: %s\n' "$project" >&2
    missing=$((missing + 1))
  fi
done <<EOF
$(find src tests -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | sort)
EOF

if [ "$missing" -gt 0 ]; then
  printf '\n%d project(s) are not in %s, so CI never builds or runs them.\n' "$missing" "$solution" >&2
  printf 'Add each one to the solution (CONTRIBUTING.md §6).\n' >&2
  exit 1
fi

printf 'Every project on disk is in %s.\n' "$solution"
