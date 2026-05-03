#!/usr/bin/env bash
# ============================================================================
#  deploy-build-system.sh
#  One-shot installer for the new FileserverDriveManager build pipeline.
#
#  Run this from your Mac, inside your local clone of the repo:
#      cd ~/FileserverDriveManager   (or wherever your clone is)
#      bash deploy-build-system.sh
#
#  What it does:
#    1. Verifies you're in the right repo
#    2. Backs up any existing build files to ./.build-system-backup/
#    3. Writes the new csproj, workflow, installer.nsi, release.sh, BUILD.md
#    4. Updates .gitignore
#    5. Commits everything
#    6. Pushes to main
#    7. (Optional) Triggers a test build via gh CLI if installed
# ============================================================================
set -euo pipefail

# -------- colour helpers --------
GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; BLUE='\033[0;34m'; NC='\033[0m'
say()  { echo -e "${BLUE}==>${NC} $*"; }
ok()   { echo -e "${GREEN}OK${NC}  $*"; }
warn() { echo -e "${YELLOW}WARN${NC} $*"; }
die()  { echo -e "${RED}FAIL${NC} $*"; exit 1; }

# -------- 1. sanity checks --------
say "Checking environment"

[[ -d .git ]] || die "Not in a git repo. cd into your FileserverDriveManager clone first."

REMOTE=$(git config --get remote.origin.url || echo "")
if [[ "$REMOTE" != *"FileserverDriveManager"* ]]; then
  warn "Remote origin is: $REMOTE"
  read -r -p "This doesn't look like the FileserverDriveManager repo. Continue anyway? [y/N] " ans
  [[ "$ans" =~ ^[Yy]$ ]] || die "Aborted."
fi

BRANCH=$(git rev-parse --abbrev-ref HEAD)
[[ "$BRANCH" == "main" ]] || die "You're on '$BRANCH'. Switch to main first: git checkout main"

if ! git diff --quiet || ! git diff --cached --quiet; then
  warn "You have uncommitted changes."
  git status --short
  read -r -p "Commit them as part of this deploy? [y/N] " ans
  [[ "$ans" =~ ^[Yy]$ ]] || die "Aborted. Commit or stash first."
fi

ok "Environment looks good"

# -------- 2. backup --------
say "Backing up existing build files to .build-system-backup/"
BACKUP_DIR=".build-system-backup-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP_DIR"

for f in FileserverDriveManager.csproj installer.nsi release.sh BUILD.md build.ps1 .github/workflows/build-and-release.yml; do
  if [[ -f "$f" ]]; then
    mkdir -p "$BACKUP_DIR/$(dirname "$f")"
    cp "$f" "$BACKUP_DIR/$f"
    echo "  backed up: $f"
  fi
done
ok "Backup at $BACKUP_DIR/"

# -------- 3. write new files --------
say "Writing new build system files"

mkdir -p .github/workflows

# ---------- FileserverDriveManager.csproj ----------
cat > FileserverDriveManager.csproj <<'CSPROJ_EOF'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>

    <AssemblyName>FileserverDriveManager</AssemblyName>
    <RootNamespace>FileserverDriveManager</RootNamespace>
    <Version>1.50.0</Version>
    <FileVersion>1.50.0.0</FileVersion>
    <AssemblyVersion>1.50.0.0</AssemblyVersion>
    <Company>Dyna Training</Company>
    <Product>Fileserver Drive Manager</Product>

    <!-- Single-file publishing settings.
         SelfContained and RuntimeIdentifier are intentionally NOT here.
         They are passed on the CLI so the workflow has full control and
         so IDEs don't auto-publish framework-dependent debug builds. -->
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <DebugType>embedded</DebugType>
    <PublishReadyToRun>false</PublishReadyToRun>
    <PublishTrimmed>false</PublishTrimmed>

    <ApplicationIcon Condition="Exists('icon.ico')">icon.ico</ApplicationIcon>
    <NoWarn>$(NoWarn);IL3000</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <None Include="logo.png" Condition="Exists('logo.png')">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Include="icon.png" Condition="Exists('icon.png')">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
CSPROJ_EOF
ok "FileserverDriveManager.csproj"

# ---------- .github/workflows/build-and-release.yml ----------
cat > .github/workflows/build-and-release.yml <<'WORKFLOW_EOF'
name: Build and Release

on:
  push:
    tags:
      - 'v*'
  workflow_dispatch:
    inputs:
      version:
        description: 'Version (e.g. 1.50) - artifact-only build, no release'
        required: false
        default: '0.0-test'

permissions:
  contents: write

jobs:
  build:
    runs-on: windows-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Determine version
        id: version
        shell: pwsh
        run: |
          if ($env:GITHUB_REF -like 'refs/tags/v*') {
            $v = $env:GITHUB_REF -replace 'refs/tags/v',''
            $isRelease = 'true'
          } else {
            $v = '${{ github.event.inputs.version }}'
            if (-not $v) { $v = '0.0-test' }
            $isRelease = 'false'
          }
          Write-Host "Version: $v"
          Write-Host "Is release: $isRelease"
          "version=$v"          | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          "is_release=$isRelease" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

      - name: Set up .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Clean any cached build artifacts
        shell: pwsh
        run: |
          if (Test-Path bin) { Remove-Item -Recurse -Force bin }
          if (Test-Path obj) { Remove-Item -Recurse -Force obj }
          Write-Host "Workspace cleaned."

      - name: Publish self-contained single-file exe
        shell: pwsh
        run: |
          dotnet publish FileserverDriveManager.csproj `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            --output publish `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:IncludeAllContentForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=embedded
          if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

      - name: Verify self-contained build
        shell: pwsh
        run: |
          $publishDir = "publish"
          $files = Get-ChildItem $publishDir -File
          Write-Host "Files in publish output:"
          $files | ForEach-Object { Write-Host "  $($_.Name)  ($([math]::Round($_.Length/1MB,2)) MB)" }

          $strayDlls = $files | Where-Object {
            $_.Extension -eq '.dll' -and
            ($_.Name -like 'System.*' -or $_.Name -like 'Microsoft.*' -or $_.Name -like 'netstandard.*')
          }
          if ($strayDlls.Count -gt 0) {
            Write-Host "::error::Framework-dependent build detected. Self-contained flags did not take effect."
            $strayDlls | ForEach-Object { Write-Host "  $($_.Name)" }
            exit 1
          }

          $exe = Join-Path $publishDir "FileserverDriveManager.exe"
          if (-not (Test-Path $exe)) {
            Write-Host "::error::Expected exe not produced: $exe"
            exit 1
          }

          $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 2)
          Write-Host "FileserverDriveManager.exe size: $sizeMb MB"

          if ($sizeMb -lt 40) {
            Write-Host "::error::Exe is only $sizeMb MB. Self-contained .NET 8 should be 60+ MB. Build is framework-dependent."
            exit 1
          }

          Write-Host "Verification passed: build is self-contained."

      - name: Embed icon (if icon.ico is present)
        shell: pwsh
        run: |
          if (-not (Test-Path icon.ico)) {
            Write-Host "icon.ico not found - skipping icon embed"
            exit 0
          }
          Invoke-WebRequest -Uri 'https://www.angusj.com/resourcehacker/resource_hacker.zip' -OutFile rh.zip
          Expand-Archive -Path rh.zip -DestinationPath ResourceHacker -Force
          & .\ResourceHacker\ResourceHacker.exe `
            -open  "publish\FileserverDriveManager.exe" `
            -save  "publish\FileserverDriveManager.exe" `
            -action addoverwrite -res icon.ico -mask "ICONGROUP,1,1033"
          Start-Sleep -Seconds 2
          Write-Host "Icon embedded."

      - name: Install NSIS
        if: hashFiles('installer.nsi') != ''
        run: choco install nsis -y --no-progress
        shell: pwsh

      - name: Build NSIS installer
        if: hashFiles('installer.nsi') != ''
        shell: pwsh
        run: |
          $version = "${{ steps.version.outputs.version }}"
          & "C:\Program Files (x86)\NSIS\makensis.exe" `
            "/DVERSION=$version" `
            "/DSOURCE_EXE=publish\FileserverDriveManager.exe" `
            installer.nsi
          if ($LASTEXITCODE -ne 0) { throw "NSIS build failed" }

      - name: Stage artifacts
        shell: pwsh
        run: |
          $version = "${{ steps.version.outputs.version }}"
          New-Item -ItemType Directory -Force -Path artifacts | Out-Null
          Copy-Item "publish\FileserverDriveManager.exe" "artifacts\FileserverDriveManager-v$version.exe"
          $installer = Get-ChildItem "Drive Manager V*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
          if ($installer) {
            Copy-Item $installer.FullName "artifacts\$($installer.Name)"
          }
          Get-ChildItem artifacts | ForEach-Object {
            Write-Host "  $($_.Name)  ($([math]::Round($_.Length/1MB,2)) MB)"
          }

      - name: Upload build artifacts
        uses: actions/upload-artifact@v4
        with:
          name: FileserverDriveManager-v${{ steps.version.outputs.version }}
          path: artifacts/*
          retention-days: 30

      - name: Create GitHub Release
        if: steps.version.outputs.is_release == 'true'
        uses: softprops/action-gh-release@v2
        with:
          tag_name: v${{ steps.version.outputs.version }}
          name: Fileserver Drive Manager v${{ steps.version.outputs.version }}
          generate_release_notes: true
          files: artifacts/*
          fail_on_unmatched_files: true
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
WORKFLOW_EOF
ok ".github/workflows/build-and-release.yml"

# ---------- installer.nsi ----------
cat > installer.nsi <<'NSI_EOF'
; ============================================================================
;  Fileserver Drive Manager - NSIS Installer
;  IMPORTANT: This installer bundles a SELF-CONTAINED .NET 8 exe.
;  There is NO .NET runtime check, no download prompt, nothing.
;  The exe runs on a clean Windows 10/11 box without .NET installed.
; ============================================================================

!ifndef VERSION
  !define VERSION "0.0-test"
!endif

!ifndef SOURCE_EXE
  !define SOURCE_EXE "publish\FileserverDriveManager.exe"
!endif

!define APP_NAME       "Fileserver Drive Manager"
!define APP_PUBLISHER  "Dyna Training"
!define APP_EXE        "FileserverDriveManager.exe"
!define APP_REGKEY     "Software\Microsoft\Windows\CurrentVersion\Uninstall\FileserverDriveManager"
!define APP_INSTALL_DIR "$PROGRAMFILES64\FileserverDriveManager"

Name "${APP_NAME} ${VERSION}"
OutFile "Drive Manager V${VERSION}.exe"
InstallDir "${APP_INSTALL_DIR}"
InstallDirRegKey HKLM "${APP_REGKEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
Unicode true
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON   "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN       "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT  "Launch ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${VERSION}.0.0"
VIAddVersionKey "ProductName"     "${APP_NAME}"
VIAddVersionKey "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "FileDescription" "${APP_NAME} Installer"
VIAddVersionKey "LegalCopyright"  "(C) ${APP_PUBLISHER}"

Section "Install" SEC_INSTALL
  nsExec::Exec 'taskkill /F /IM "${APP_EXE}"'
  Sleep 500

  SetOutPath "$INSTDIR"
  SetOverwrite on

  File "/oname=${APP_EXE}" "${SOURCE_EXE}"
  File /nonfatal "LICENSE.txt"
  File /nonfatal "README.md"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut  "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut  "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut  "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr   HKLM "${APP_REGKEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr   HKLM "${APP_REGKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKLM "${APP_REGKEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKLM "${APP_REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${APP_REGKEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKLM "${APP_REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${APP_REGKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${APP_REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${APP_REGKEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${APP_REGKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM "${APP_EXE}"'
  Sleep 500

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  DeleteRegKey HKLM "${APP_REGKEY}"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "FileserverDriveManager"
SectionEnd
NSI_EOF
ok "installer.nsi"

# ---------- release.sh ----------
cat > release.sh <<'RELEASE_EOF'
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
RELEASE_EOF
chmod +x release.sh
ok "release.sh (executable)"

# ---------- BUILD.md ----------
cat > BUILD.md <<'BUILD_EOF'
# Build & Release System

This repo builds a **truly self-contained** Windows .NET 8 WinForms app — users install it on a clean Windows box without ever needing the .NET runtime.

## How it works

You develop on Mac. **GitHub Actions does the build** on a fresh Windows runner. There is no local Windows build step. This is by design — every prior failure on this project was caused by stale `bin/` and `obj/` folders on the Windows PC, or by `dotnet build` running before `dotnet publish` and poisoning the cache. A fresh CI runner cannot have stale state, so it cannot have those bugs.

## Cutting a release (from your Mac)

```bash
./release.sh 1.51
```

That bumps the version in the csproj, commits, tags `v1.51`, and pushes. GitHub Actions takes over and produces a release ~3-5 minutes later with two attached files:

- `FileserverDriveManager-v1.51.exe` — the bare self-contained app (~70 MB)
- `Drive Manager V1.51.exe` — the NSIS installer (~65 MB)

## Testing without releasing

GitHub → **Actions** → **Build and Release** → **Run workflow** → enter `0.99-test` → Run. The exe and installer come down as a workflow artifact. No release is created.

## Why this build cannot ship a framework-dependent exe

The workflow has a verification step that fails the build if:
1. The publish output contains stray runtime DLLs (`System.*.dll`, `Microsoft.*.dll`).
2. The final exe is under 40 MB.

If either trips, the workflow stops with a clear error. You cannot accidentally publish a release that prompts for the .NET runtime.
BUILD_EOF
ok "BUILD.md"

# -------- 4. update .gitignore --------
say "Updating .gitignore"
touch .gitignore
for entry in "bin/" "obj/" "publish/" "artifacts/" "Drive Manager V*.exe" "*.user" ".vs/" ".build-system-backup-*/" "logo.png" "icon.png"; do
  if ! grep -qxF "$entry" .gitignore; then
    echo "$entry" >> .gitignore
  fi
done
ok ".gitignore updated"

# -------- 5. remove old build.ps1 if present --------
if [[ -f build.ps1 ]]; then
  say "Removing obsolete build.ps1 (replaced by GitHub Actions)"
  git rm -f build.ps1 2>/dev/null || rm -f build.ps1
  ok "build.ps1 removed"
fi

# -------- 6. commit and push --------
say "Committing"
git add -A FileserverDriveManager.csproj installer.nsi release.sh BUILD.md .gitignore .github/
git status --short

if git diff --cached --quiet; then
  warn "No changes to commit (already up to date?)"
else
  git commit -m "Rebuild release pipeline: CI-only, self-contained, verified

- Move all build steps to GitHub Actions on a fresh Windows runner
- Pass --runtime and --self-contained on the CLI (not in csproj) so they
  cannot be silently overridden
- Add verification step that fails the build if stray runtime DLLs appear
  or if the exe is too small to be self-contained
- Strip all .NET runtime checks from installer.nsi
- Add release.sh for one-command Mac-side releases"
  ok "Committed"
fi

say "Pushing to origin/main"
git push origin main
ok "Pushed"

# -------- 7. trigger test build via gh CLI if available --------
echo
if command -v gh >/dev/null 2>&1; then
  say "GitHub CLI detected. Trigger a test build now? (no release will be created)"
  read -r -p "Trigger test build [Y/n]? " ans
  if [[ ! "$ans" =~ ^[Nn]$ ]]; then
    gh workflow run "Build and Release" -f version="1.50-test" || warn "Could not trigger workflow automatically. Trigger manually from the Actions tab."
    sleep 3
    echo
    say "Latest workflow runs:"
    gh run list --workflow="Build and Release" --limit 3 || true
    echo
    echo "Watch progress with:  gh run watch"
    echo "Or in browser:        https://github.com/HybridRCG/FileserverDriveManager/actions"
  fi
else
  warn "GitHub CLI ('gh') not installed on this Mac."
  echo "    Install with: brew install gh"
  echo "    Then trigger a test build manually:"
  echo "      https://github.com/HybridRCG/FileserverDriveManager/actions"
  echo "      → Build and Release → Run workflow → version: 1.50-test"
fi

echo
echo -e "${GREEN}=========================================${NC}"
echo -e "${GREEN}  Deploy complete${NC}"
echo -e "${GREEN}=========================================${NC}"
echo
echo "Next:"
echo "  1. Watch the test build go green at https://github.com/HybridRCG/FileserverDriveManager/actions"
echo "  2. Download the artifact and confirm the exe runs on a clean Windows machine"
echo "  3. When confirmed working, cut a real release:  ./release.sh 1.51"
