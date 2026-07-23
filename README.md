# HoomNote

HoomNote is a local-first Windows note-taking application for typed notes,
pressure-independent vector ink, imported PDFs and presentations, and Samsung
Notes documents. It is built with .NET 10, WinUI 3, Win2D, and SQLite.

## Run a release build

```powershell
.\build-release.ps1
```

The click-to-run app is created at `artifacts\HoomNote\HoomNote.exe`, and the
one-click installer is created at `artifacts\HoomNote-Setup.exe`.

## Publish an OTA update

1. Increment the version in `src\HoomNote.App\HoomNote.App.csproj`.
2. Update `docs\release-notes.md`.
3. Commit and push the source changes to `main`.
4. Run:

```powershell
.\publish-release.ps1
```

The script builds HoomNote and publishes the versioned Velopack assets to the
public GitHub Release. Installed copies discover that release automatically,
then offer to download, apply, and restart.

See [distribution and signing](docs/DISTRIBUTION.md) for production details.
