#!/usr/bin/env bash
# Puts the TypeScript compiler that Sightline reads JavaScript and TypeScript with into
# src/Co.Revington.Sightline/analyzers/typescript/vendor/typescript: the files it needs from the npm
# package, unmodified, after checking the download against the registry's checksum.
#
#   bash .github/scripts/vendor-typescript.sh 6.0.3
#
# TypeScript 7 no longer offers the compiler API that Sightline uses, so this stays on 6.0.x.
# Needs npm, tar and openssl. Update THIRD-PARTY-NOTICES.md with the version.
set -euo pipefail

version="${1:?Give the TypeScript version, such as 6.0.3}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
target="$root/src/Co.Revington.Sightline/analyzers/typescript/vendor/typescript"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

expected="$(npm view "typescript@$version" dist.integrity)"
(cd "$work" && npm pack "typescript@$version" --silent >/dev/null)
tarball="$work/typescript-$version.tgz"
actual="sha512-$(openssl dgst -sha512 -binary "$tarball" | openssl base64 -A)"
if [ "$actual" != "$expected" ]; then
  echo "The download of typescript@$version does not match the checksum on the registry." >&2
  exit 1
fi

mkdir -p "$work/x"
tar -xzf "$tarball" -C "$work/x" --wildcards \
  'package/package.json' 'package/LICENSE.txt' 'package/ThirdPartyNoticeText.txt' \
  'package/lib/typescript.js' 'package/lib/lib*.d.ts'

rm -rf "$target"
mkdir -p "$target/lib"
cp "$work/x/package/package.json" "$work/x/package/LICENSE.txt" "$work/x/package/ThirdPartyNoticeText.txt" "$target/"
cp "$work/x/package/lib/typescript.js" "$work/x/package/lib/"lib*.d.ts "$target/lib/"
echo "TypeScript $version is in ${target#"$root"/}"
