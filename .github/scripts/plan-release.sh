#!/usr/bin/env bash
# Decides whether this push becomes a release, which version it gets, and writes the release notes.
#
# Tags named vX.Y.Z are the record of what has been released. The next version comes from
# the commit messages since the last tag:
#
#   feat!: ...  or a "BREAKING CHANGE:" line   -> major  (minor while the version is still 0.x)
#   feat: ...                                  -> minor
#   anything else                              -> patch
#
# A higher <VersionPrefix> in the project file wins. Set it to 1.0.0 to go stable.
#
# A push is released only when something under src/ changed since the last tag.
# Starting the workflow by hand releases regardless.
#
# Inputs (environment): NUGET_USER, MANUAL. Outputs: version, release, and release-notes.md.

set -euo pipefail

project="src/Sightline/Sightline.csproj"
notes="release-notes.md"
output="${GITHUB_OUTPUT:-/dev/stdout}"
repo_url="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-ravindu-rev/sightline}"

breaking='^[a-z]+(\([^)]*\))?!:|^BREAKING CHANGE:'
feature='^feat(\([^)]*\))?:'
fix='^fix(\([^)]*\))?:'

base=$(sed -n 's:.*<VersionPrefix>\([0-9][0-9.]*\)</VersionPrefix>.*:\1:p' "$project" | head -n 1)
last=$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -n 1)

# ---------------------------------------------------------------- version

if [ -z "$last" ]; then
  # Nothing released yet: start at the version written in the project file.
  next="$base"
  range="HEAD"
  changed=true
else
  range="$last..HEAD"
  if git diff --quiet "$last" HEAD -- src; then changed=false; else changed=true; fi

  IFS=. read -r major minor patch <<< "${last#v}"
  messages=$(git log "$range" --format='%s%n%b')

  if grep -Eq "$breaking" <<< "$messages"; then bump=major
  elif grep -Eq "$feature" <<< "$messages"; then bump=minor
  else bump=patch
  fi
  if [ "$major" -eq 0 ] && [ "$bump" = major ]; then bump=minor; fi

  case "$bump" in
    major) major=$((major + 1)); minor=0; patch=0 ;;
    minor) minor=$((minor + 1)); patch=0 ;;
    patch) patch=$((patch + 1)) ;;
  esac
  next="$major.$minor.$patch"

  # The project file can ask for a higher version than the commits would give.
  next=$(printf '%s\n%s\n' "$next" "$base" | sort -V | tail -n 1)
fi

# ---------------------------------------------------------------- release or not

release=true
reason=""
if [ "$changed" = false ] && [ "${MANUAL:-false}" != true ]; then
  release=false
  reason="Nothing under src/ changed since $last, so there is nothing new to publish."
elif [ -z "${NUGET_USER:-}" ]; then
  release=false
  reason="NuGet publishing is not set up yet: the repository variable NUGET_USER is empty. See 'Releasing' in README.md."
fi

# ---------------------------------------------------------------- release notes

commits=$(git log "$range" --no-merges --format='%s (%h)')

section() {   # $1 = heading, $2 = pattern of commit subjects that belong under it
  local lines
  lines=$(grep -E "$2" <<< "$commits" || true)
  if [ -z "$lines" ]; then return 0; fi
  echo "### $1"
  echo
  # Drop the "feat(scope): " prefix; the heading already says what kind of change it is.
  sed -E 's/^[a-z]+(\([^)]*\))?!?: *//; s/^/- /' <<< "$lines"
  echo
}

{
  section "Breaking changes" '^[a-z]+(\([^)]*\))?!:'
  section "New" "$feature"
  section "Fixed" "$fix"

  other=$(grep -Ev "$feature|$fix|^[a-z]+(\([^)]*\))?!:" <<< "$commits" || true)
  if [ -n "$other" ]; then
    echo "### Other changes"
    echo
    sed -E 's/^/- /' <<< "$other"
    echo
  fi

  echo "### Install or update"
  echo
  echo '```'
  echo "dotnet tool install --global Sightline --version $next"
  echo "dotnet tool update --global Sightline"
  echo '```'
  if [ -n "$last" ]; then
    echo
    echo "All changes: $repo_url/compare/$last...v$next"
  fi
} > "$notes"

# ---------------------------------------------------------------- report

{
  echo "version=$next"
  echo "release=$release"
} >> "$output"

if [ "$release" = true ]; then
  echo "::notice title=Release::Publishing Sightline $next (previous: ${last:-none})"
else
  echo "::notice title=No release::$reason"
fi

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    if [ "$release" = true ]; then echo "## Releasing Sightline $next"; else echo "## No release"; echo; echo "$reason"; fi
    echo
    cat "$notes"
  } >> "$GITHUB_STEP_SUMMARY"
fi
