# HoomNote distribution and OTA updates

HoomNote uses Velopack 1.2.0 for per-user Windows installation and OTA updates.
The installer does not require administrator privileges and installs under the
current user's local application data directory.

## Build a release

From the repository root:

```powershell
.\build-release.ps1
```

The build creates:

- `artifacts\HoomNote-Setup.exe` — the one-click installer to send to users.
- `artifacts\HoomNote-Portable.zip` — a portable Velopack build.
- `artifacts\HoomNote\HoomNote.exe` — the unpackaged development/verification build.
- `artifacts\HoomNote-Releases\` — the update feed and packages to upload.

By default, installed builds check the public
`austinconnor/HoomNote` GitHub Releases feed. A different GitHub repository or
static HTTPS feed can be supplied with `-UpdateUrl` or the
`HOOMNOTE_UPDATE_URL` environment variable.

## Publish an OTA release

1. Increment `<Version>` in `src\HoomNote.App\HoomNote.App.csproj`.
2. Update `docs\release-notes.md`.
3. Commit and push the source changes to `main`.
4. Run:

   ```powershell
   .\publish-release.ps1
   ```

The publish script builds the release, creates the matching `vX.Y.Z` Git tag
and GitHub Release, and uploads the installer, portable ZIP, update packages,
feed manifest, and checksums together. Send new users the
`HoomNote-win-x64-Setup.exe` asset from the latest GitHub Release.

Installed copies check the feed after startup without blocking the editor.
When an update exists, HoomNote asks permission, downloads it, flushes autosave,
then applies the package and restarts. The raw `artifacts\HoomNote` development
folder is intentionally not considered an installed build and will not self-update.

## Signing

Unsigned installers work but can trigger Windows SmartScreen. Public builds
should be code-signed. The release script supports either:

```powershell
.\publish-release.ps1 `
  -AzureTrustedSignFile "C:\secure\metadata.json"
```

or:

```powershell
.\publish-release.ps1 `
  -SignParams '/td sha256 /fd sha256 /f "C:\secure\certificate.pfx" /tr "https://timestamp.digicert.com"'
```

The same values can be provided through `HOOMNOTE_AZURE_TRUSTED_SIGN_FILE` or
`HOOMNOTE_SIGN_PARAMS`, which keeps signing secrets out of source control.
