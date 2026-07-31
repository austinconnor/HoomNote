# HoomNote repository instructions

## General rules

- Use PowerShell commands from the repository root.
- Preserve unrelated user changes in a dirty working tree. Never discard or
  rewrite them to prepare a release.
- Do not publish, create a GitHub release, or push release changes unless the
  user explicitly asks to publish.
- Do not launch or control the HoomNote UI when the user asks for a build only.
  Package the app and ask the user to verify it.
- When working in Python, use `uv`.

## Version and release metadata

Before creating a release build:

1. Increment `<Version>` in
   `src/HoomNote.App/HoomNote.App.csproj`. Published versions are immutable;
   `publish-release.ps1` intentionally refuses to replace an existing tag.
2. Change the heading in `docs/release-notes.md` to the same version and
   summarize every user-visible change included since the previous release.
3. Confirm the app and release notes contain the same version:

   ```powershell
   rg -n "<Version>|^# HoomNote " src/HoomNote.App/HoomNote.App.csproj docs/release-notes.md
   ```

## Validation before staging

Run the complete test suite and a Release build:

```powershell
dotnet test HoomNote.slnx -c Release --no-restore
dotnet build src/HoomNote.App/HoomNote.App.csproj -c Release --no-restore
git diff --check
```

If dependencies changed or `--no-restore` cannot run, restore normally and
repeat the commands. Do not stage or publish a release with failing tests,
build warnings that indicate a packaging/runtime problem, or whitespace
errors.

For a user verification build that must not be published:

```powershell
.\build-release.ps1 -Runtime win-x64
```

Give the user these stable paths:

- `artifacts\HoomNote\HoomNote.exe` — direct-click portable app
- `artifacts\HoomNote-Setup.exe` — one-click installer

Wait for the user to verify the build when they requested testing before
publication.

## Inspect and stage the release

Before staging, synchronize read-only remote state and confirm that local
`main` is based on `origin/main`:

```powershell
git fetch origin main --tags
git status -sb
git log --oneline --decorate -6
gh release list --repo austinconnor/HoomNote --limit 5
```

Review the actual release scope:

```powershell
git status --short
git diff --stat
git diff
```

Stage explicit intended paths rather than blindly staging the whole working
tree:

```powershell
git add -- <intended-paths>
git diff --cached --check
git diff --cached --stat
git diff --cached
```

The staged set must include the version file, release notes, implementation,
and relevant tests. It must not include local databases, logs, caches,
temporary files, secrets, or generated `artifacts` unless the repository
explicitly starts tracking them.

Commit only after the staged diff matches the tested build:

```powershell
git commit -m "Release HoomNote <version>"
```

## Push and publish an OTA update

Publishing requires an authenticated GitHub CLI session:

```powershell
gh auth status
```

Push the tested commit before creating the release, because the GitHub release
targets `main`:

```powershell
git push origin main
```

Then run the supported publishing entry point:

```powershell
.\publish-release.ps1 -Runtime win-x64 -Repository austinconnor/HoomNote
```

Every OTA GitHub release must include the change notes from
`docs/release-notes.md` in its GitHub release body. After publishing, verify
that the public release body is populated and matches the released version. If
the publishing script did not attach the notes, add them before reporting the
release as complete:

```powershell
gh release edit v<version> --repo austinconnor/HoomNote `
  --notes-file docs/release-notes.md
```

Do not manually recreate the Velopack command or upload a partial asset set.
The script rebuilds the portable app, creates the installer and OTA feed,
generates SHA-256 checksums, and publishes all files in
`artifacts\HoomNote-Releases` as `v<version>`.

The default installer is unsigned and can trigger Windows SmartScreen. Do not
claim it is signed. If signing is explicitly configured, use exactly one of
the supported inputs:

- `HOOMNOTE_AZURE_TRUSTED_SIGN_FILE` / `-AzureTrustedSignFile`
- `HOOMNOTE_SIGN_PARAMS` / `-SignParams`

## Verify publication

After the script succeeds, verify both Git synchronization and the public
release:

```powershell
gh release view v<version> --repo austinconnor/HoomNote `
  --json url,tagName,isDraft,isPrerelease,targetCommitish,publishedAt,body,assets
git status -sb
git rev-parse HEAD
git rev-parse origin/main
```

The release is complete only when:

- it is public (`isDraft: false`) and not a prerelease unless requested;
- it targets `main`;
- its GitHub release body contains the version-matched change notes from
  `docs/release-notes.md`;
- local `HEAD` equals `origin/main`;
- the uploaded assets include the setup executable, portable ZIP, full
  `.nupkg`, architecture-specific release feeds, and `SHA256SUMS.txt`.

Report the release URL, direct installer URL, commit hash, test totals, and
unsigned/SmartScreen status. Never report publication as successful until the
remote verification succeeds.
