#!/usr/bin/env bash
#
# Shared discovery of the EF Core projects, for the migration gates that must cover all of them.
#
# Discovery is by convention (a project directory that owns Persistence/Migrations), because a new
# module must join the gates without anyone editing a list. A convention alone fails open, though:
# a project whose migrations landed somewhere else — "dotnet ef migrations add" defaults to
# Migrations/, not Persistence/Migrations/ — would simply drop out of every gate in silence.
#
# So the discovered list is cross-checked against a second, independent source: the design-time
# DbContext factories. Every EF project needs one (dotnet ef cannot build the context without it),
# so the two sets must agree exactly. When they do not, this fails and names the project.
#
# Source this file; it defines cinomni_ef_projects, which prints one project directory per line and
# returns non-zero when discovery is inconsistent. The caller must already be at the repository
# root.

# Prints the repository-relative directory of every EF project, sorted. Returns 1 when the
# convention-based discovery and the design-time factories disagree.
cinomni_ef_projects() {
  discovered="$(
    find src -type d -path '*/Persistence/Migrations' -not -path '*/bin/*' -not -path '*/obj/*' |
      sed 's|/Persistence/Migrations$||' |
      LC_ALL=C sort -u
  )"

  # <project>/Persistence/<Name>DbContextFactory.cs -> <project>
  factories="$(
    find src -name '*DbContextFactory.cs' -not -path '*/bin/*' -not -path '*/obj/*' |
      sed 's|/Persistence/[^/]*$||' |
      LC_ALL=C sort -u
  )"

  if [ -z "$factories" ]; then
    printf 'Found no *DbContextFactory.cs under src/. Expected at least the Operations platform schema.\n' >&2
    return 1
  fi

  inconsistent=0

  while IFS= read -r project; do
    [ -n "$project" ] || continue
    if ! grep -qxF -- "$project" <<<"$discovered"; then
      printf 'EF PROJECT NOT COVERED BY THE MIGRATION GATES: %s\n' "$project" >&2
      printf '  It owns a design-time DbContext factory but no %s/Persistence/Migrations directory.\n' "$project" >&2
      printf '  Generate migrations with --output-dir Persistence/Migrations (CONTRIBUTING.md), or the\n' >&2
      printf '  project silently drops out of the apply, upgrade and drift checks.\n\n' >&2
      inconsistent=1
    fi
  done <<EOF
$factories
EOF

  while IFS= read -r project; do
    [ -n "$project" ] || continue
    if ! grep -qxF -- "$project" <<<"$factories"; then
      printf 'EF PROJECT WITHOUT A DESIGN-TIME FACTORY: %s\n' "$project" >&2
      printf '  It owns migrations but no *DbContextFactory.cs, so dotnet ef cannot build its context.\n\n' >&2
      inconsistent=1
    fi
  done <<EOF
$discovered
EOF

  if [ "$inconsistent" -ne 0 ]; then
    return 1
  fi

  printf '%s\n' "$discovered"
}
