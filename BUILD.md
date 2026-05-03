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
