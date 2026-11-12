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
# A higher <VersionPrefix> in src/Directory.Build.props wins. Set it to 1.0.0 to go stable.
#
# A push is released only when something under src/ changed since the last tag.
# Starting the workflow by hand releases regardless.
#
# The release notes group commits by type. ci, test, chore, docs, build and style commits
# are left out, because they do not change what users get.
#
# Inputs (environment): NUGET_USER, MANUAL, NOTES_ONLY (write the notes, report nothing).
# Outputs: version and release in GITHUB_OUTPUT, and release-notes.md.

set -euo pipefail

project="src/Directory.Build.props"       # holds <VersionPrefix> for both packages
package="Co.Revington.Sightline"
notes="release-notes.md"
output="${GITHUB_OUTPUT:-/dev/stdout}"
repo_url="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-ravindu-rev/sightline}"

scope='(\([^)]*\))?'
breaking_subject="^[a-z]+$scope!:"
feature="^feat$scope:"
fix="^fix$scope:"
hidden="^(ci|test|chore|docs|build|style)$scope:"

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

  if grep -Eq "$breaking_subject|^BREAKING CHANGE:" <<< "$messages"; then bump=major
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

# One line per commit: "subject (abc1234)". A "BREAKING CHANGE:" line in the body marks the
# subject with "!", so the commit is listed under breaking changes.
commits=$(git log "$range" --no-merges --format='%h' | while read -r sha; do
  subject=$(git log -1 --format='%s' "$sha")
  if git log -1 --format='%b' "$sha" | grep -q '^BREAKING CHANGE:'; then
    subject=$(sed -E 's/^([a-z]+(\([^)]*\))?):/\1!:/' <<< "$subject")
  fi
  echo "$subject ($sha)"
done)

listed=false
section() {   # $1 = heading, $2 = pattern of commit subjects that belong under it
  local lines
  lines=$(grep -E "$2" <<< "$commits" || true)
  if [ -z "$lines" ]; then return 0; fi
  listed=true
  echo "### $1"
  echo
  # Drop the "feat(scope): " prefix; the heading already says what kind of change it is.
  sed -E 's/^[a-z]+(\([^)]*\))?!?: *//; s/^/- /' <<< "$lines"
  echo
}

{
  if [ -z "$last" ]; then
    # Before the first release there is nothing to fix or change, so only features are listed.
    echo "First release of Sightline."
    echo
    section "New" "$feature"
  else
    section "Breaking changes" "$breaking_subject"
    section "New" "$feature"
    section "Fixed" "$fix"
    other=$(grep -Ev "$breaking_subject|$feature|$fix|$hidden" <<< "$commits" || true)
    if [ -n "$other" ]; then
      listed=true
      echo "### Other changes"
      echo
      sed -E 's/^[a-z]+(\([^)]*\))?: *//; s/^/- /' <<< "$other"
      echo
    fi
    if [ "$listed" = false ]; then
      echo "Maintenance release with no user-facing changes."
      echo
    fi
  fi

  echo "### Install or update"
  echo
  echo "For a solution: run this in its folder. It sets the solution up, or moves one that is set up to this version."
  echo
  echo '```sh'
  echo "dnx $package@$next init"
  echo '```'
  echo
  echo "Inside the app you run, so the map opens beside it in every Debug run:"
  echo
  echo '```sh'
  echo "dotnet add package $package.Hosting --version $next"
  echo '```'
  echo
  echo "Just for yourself:"
  echo
  echo '```sh'
  echo "dotnet tool install --global $package --version $next"
  echo "dotnet tool update --global $package"
  echo '```'
  if [ -n "$last" ]; then
    echo
    echo "All changes: $repo_url/compare/$last...v$next"
  fi
} > "$notes"

if [ "${NOTES_ONLY:-false}" = true ]; then exit 0; fi

# ---------------------------------------------------------------- report

{
  echo "version=$next"
  echo "release=$release"
} >> "$output"

if [ "$release" = true ]; then
  echo "::notice title=Release::Publishing $package $next (previous: ${last:-none})"
else
  echo "::notice title=No release::$reason"
fi

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    if [ "$release" = true ]; then echo "## Releasing $package $next"; else echo "## No release"; echo; echo "$reason"; fi
    echo
    cat "$notes"
  } >> "$GITHUB_STEP_SUMMARY"
fi
