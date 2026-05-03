#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  echo "Usage: $0 <version>     e.g. $0 1.51"
  exit 1
fi

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
  echo "Version must be like 1.51 or 1.51.0"
  exit 1
fi

TAG="v${VERSION}"

git fetch origin
BRANCH=$(git rev-parse --abbrev-ref HEAD)
[[ "$BRANCH" == "main" ]] || { echo "Switch to main first."; exit 1; }

if git rev-parse "$TAG" >/dev/null 2>&1; then
  echo "Tag $TAG already exists. Bump the version."
  exit 1
fi

CSPROJ="FileserverDriveManager.csproj"
if [[ -f "$CSPROJ" ]]; then
  sed -i '' -E "s|<Version>[^<]+</Version>|<Version>${VERSION}.0</Version>|" "$CSPROJ"
  sed -i '' -E "s|<FileVersion>[^<]+</FileVersion>|<FileVersion>${VERSION}.0.0</FileVersion>|" "$CSPROJ"
  sed -i '' -E "s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>${VERSION}.0.0</AssemblyVersion>|" "$CSPROJ"
  git add "$CSPROJ"
fi

if ! git diff --cached --quiet || ! git diff --quiet; then
  git add -A
  git commit -m "Release ${TAG}"
fi

git push origin main
git tag -a "$TAG" -m "Release ${TAG}"
git push origin "$TAG"

echo
echo "Tag $TAG pushed. GitHub Actions is now building."
echo "https://github.com/HybridRCG/FileserverDriveManager/actions"
