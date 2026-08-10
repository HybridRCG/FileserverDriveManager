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
  # Normalize VERSION (2-part "X.Y" or 3-part "X.Y.Z" input) into:
  #   <Version>       - always 3-part (X.Y.Z), what NuGet/the app displays
  #   <FileVersion> / <AssemblyVersion> - always 4-part (X.Y.Z.0), the .NET-required format
  # Previously this blindly appended ".0" and ".0.0" regardless of how many
  # parts VERSION already had, which produced an invalid 5-part string
  # (e.g. "5.3.1.0.0") whenever a 3-part version was passed - .NET's SDK
  # rejects anything beyond 4 parts with CS7034.
  IFS='.' read -ra VPARTS <<< "$VERSION"
  case "${#VPARTS[@]}" in
    2) FULL_VERSION="${VERSION}.0" ;;
    3) FULL_VERSION="${VERSION}" ;;
    *) echo "Unexpected version part count for '$VERSION'"; exit 1 ;;
  esac
  FILE_VERSION="${FULL_VERSION}.0"

  sed -i '' -E "s|<Version>[^<]+</Version>|<Version>${FULL_VERSION}</Version>|" "$CSPROJ"
  sed -i '' -E "s|<FileVersion>[^<]+</FileVersion>|<FileVersion>${FILE_VERSION}</FileVersion>|" "$CSPROJ"
  sed -i '' -E "s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>${FILE_VERSION}</AssemblyVersion>|" "$CSPROJ"
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
