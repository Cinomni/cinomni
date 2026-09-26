#!/usr/bin/env bash
#
# Module boundary gate.
#
# CONTRIBUTING.md states the dependency rules are "enforced by project references — CI fails if
# broken", but nothing mechanically enforced them: a wrong <ProjectReference> compiles fine. This
# script reads every .csproj under src/, tests/ and poc/ and fails the build when a reference breaks
# one of the rules below. MSBuild already rejects a reference cycle, so cycles are not re-checked.
#
# Rules (from CONTRIBUTING.md §4):
#   1. Only the Host may reference a module implementation project. Every other project must go
#      through <Module>.Contracts. A project may reference a sibling project inside its own module
#      directory (that is one module split across assemblies, not a cross-module reference).
#   2. Cinomni.Kernel depends on nothing.
#   3. A .Contracts project holds a public surface only: it may reference Cinomni.Kernel, a shared
#      contracts project and another module's .Contracts, never Cinomni.Operations.
#   4. Nothing references the Host. The Host is the composition root, not a library.
#   5. No raw <Reference> with a HintPath. Linking a built assembly out of another project's bin/
#      is the same boundary break as a project reference, and nothing else in the tree would see it.
#
# Scope, stated rather than implied: rules 1 and 4 do not apply to tests/, which drive
# implementations and host the composition root on purpose. Rules 2, 3 and 5 apply everywhere.
#
# The reference reader parses attributes instead of matching a line, because MSBuild accepts
# spellings a line-oriented grep does not see — Include after another attribute, single quotes, or
# the element wrapped across lines — and a reference the gate cannot see is a reference it declares
# does not exist. It also counts the elements it found against the ones it parsed and fails when the
# two disagree, so this gate breaks loudly instead of quietly passing everything.
#
# Usage: bash .github/scripts/check-module-boundaries.sh [repository-root]
set -euo pipefail

root="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$root"

host_project="src/Host/Cinomni.Host/Cinomni.Host.csproj"
violations=0

# Collapse "." and ".." segments without touching the filesystem, so a reference resolves to a
# stable repository-relative path on every platform.
normalize_path() {
  printf '%s' "$1" | awk -F/ '{
    depth = 0
    for (i = 1; i <= NF; i++) {
      if ($i == "" || $i == ".") continue
      if ($i == "..") { if (depth > 0) depth--; continue }
      segment[++depth] = $i
    }
    out = ""
    for (i = 1; i <= depth; i++) out = (i == 1 ? segment[i] : out "/" segment[i])
    print out
  }'
}

# The module a project belongs to, or an empty string for platform/shared/host/test projects.
module_of() {
  case "$1" in
    src/Modules/*) printf '%s' "$1" | cut -d/ -f3 ;;
    *) printf '' ;;
  esac
}

is_contracts_project() {
  case "$1" in
    *.Contracts.csproj) return 0 ;;
    *) return 1 ;;
  esac
}

is_test_project() {
  case "$1" in
    tests/*) return 0 ;;
    *) return 1 ;;
  esac
}

report() {
  printf 'BOUNDARY VIOLATION\n  project: %s\n  reference: %s\n  reason: %s\n\n' "$1" "$2" "$3" >&2
  violations=$((violations + 1))
}

# Emits one record per reference element:
#   project-reference<TAB><Include>
#   reference<TAB><Include><TAB><HintPath>
#   summary<TAB><elements found><TAB><elements parsed>
read_references() {
  tr -d '\r' <"$1" | awk '
    function attribute(text, name,    at, rest, quote, closing) {
      at = match(text, "[ \t]" name "[ \t]*=")
      if (at == 0) return ""
      rest = substr(text, at + RLENGTH)
      quote = substr(rest, 1, 1)
      if (quote != "\"" && quote != "'"'"'") return ""
      rest = substr(rest, 2)
      closing = index(rest, quote)
      if (closing == 0) return ""
      return substr(rest, 1, closing - 1)
    }

    { document = document " " $0 }

    END {
      # Drop XML comments, which may span lines and may contain ">".
      stripped = ""
      while ((open_at = index(document, "<!--")) > 0) {
        stripped = stripped substr(document, 1, open_at - 1)
        rest = substr(document, open_at + 4)
        close_at = index(rest, "-->")
        if (close_at == 0) { document = ""; break }
        document = substr(rest, close_at + 3)
      }
      document = stripped document

      # One element per line, so an element written across several lines is still one record.
      gsub(/</, "\n<", document)
      count = split(document, elements, "\n")

      found = 0
      parsed = 0
      for (i = 1; i <= count; i++) {
        element = elements[i]
        if (element !~ /^<(ProjectReference|Reference)[ \t\/>]/) continue
        found++

        include = attribute(element, "Include")
        if (element ~ /^<ProjectReference/) {
          if (include != "") {
            parsed++
            printf "project-reference\t%s\n", include
          } else if (attribute(element, "Update") != "" || attribute(element, "Remove") != "") {
            # Update/Remove carry no new dependency.
            parsed++
          }
        } else {
          parsed++
          printf "reference\t%s\t%s\n", include, attribute(element, "HintPath")
        }
      }

      printf "summary\t%d\t%d\n", found, parsed
    }
  '
}

while IFS= read -r project; do
  project_dir="$(dirname "$project")"
  project_module="$(module_of "$project")"

  while IFS=$'\t' read -r kind first second; do
    [ -n "$kind" ] || continue

    if [ "$kind" = "summary" ]; then
      if [ "$first" != "$second" ]; then
        report "$project" "(unparsed)" \
          "found $first reference element(s) but could only read $second; this gate cannot vouch for the rest"
      fi
      continue
    fi

    # Rule 5: a raw assembly reference bypasses the project graph entirely.
    if [ "$kind" = "reference" ]; then
      if [ -n "$second" ]; then
        report "$project" "$first" \
          "a <Reference> with HintPath '$second' links a built assembly directly; use a ProjectReference or a package"
      fi
      continue
    fi

    reference="$(printf '%s' "$first" | tr '\\' '/')"
    target="$(normalize_path "$project_dir/$reference")"
    target_module="$(module_of "$target")"

    if [ ! -f "$target" ]; then
      report "$project" "$target" "the referenced project file does not exist"
      continue
    fi

    # Rule 1: module implementations are private to the Host (tests drive them by design).
    if [ -n "$target_module" ] && ! is_contracts_project "$target" && ! is_test_project "$project"; then
      if [ "$project" != "$host_project" ] && [ "$project_module" != "$target_module" ]; then
        report "$project" "$target" \
          "only the Host may reference the implementation of module '$target_module'; reference Cinomni.$target_module.Contracts instead"
      fi
    fi

    # Rule 2: the kernel is the bottom of the graph.
    case "$project" in
      */Cinomni.Kernel/Cinomni.Kernel.csproj)
        report "$project" "$target" "Cinomni.Kernel must depend on nothing"
        ;;
    esac

    # Rule 3: contracts stay free of the platform runtime.
    if is_contracts_project "$project"; then
      case "$target" in
        */Cinomni.Operations/Cinomni.Operations.csproj)
          report "$project" "$target" \
            "a .Contracts project is a public surface and must not depend on Cinomni.Operations"
          ;;
      esac
    fi

    # Rule 4: nothing depends on the composition root (a test host may, and does).
    if [ "$target" = "$host_project" ] && ! is_test_project "$project"; then
      report "$project" "$target" "the Host is the composition root and must not be referenced"
    fi
  done <<EOF
$(read_references "$project")
EOF
done <<EOF
$(find src tests poc -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort)
EOF

if [ "$violations" -gt 0 ]; then
  printf 'Module boundary check failed: %d violation(s).\n' "$violations" >&2
  exit 1
fi

printf 'Module boundaries OK.\n'
