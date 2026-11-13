# Moving the project to another PC

The GitHub repositories are the migration source of truth. Generated `bin`, `obj`,
`.codex-build`, `artifacts`, and `dist` directories are intentionally not versioned;
the build scripts recreate them.

## 1. Install the development tools

- Git and GitHub CLI (`gh`), then run `gh auth login`.
- .NET SDK 10.0. The last verified development machine used SDK `10.0.302`.
- Inno Setup 6 for the complete installer.
- Node.js only when running the JavaScript regression tests.

## 2. Restore the source

```powershell
gh repo clone toonymak1993/tools-for-steam
Set-Location tools-for-steam
git switch codex/full-project-backup
dotnet restore .\SteamLoader.slnx
dotnet build .\SteamLoader.slnx
```

The explicit branch switch is required until the backup pull request has been
merged into `main`.

## 3. Signing assets

The Xbox Mode Gaming Home package was removed, so the installer build no longer
needs the Xbox Host signing certificate. The encrypted copy in the private
repository `toonymak1993/tools-for-steam-signing-backup` is only needed again if
that package ever comes back.

## 4. Verify and publish

```powershell
dotnet test .\SteamLoader.slnx
powershell -ExecutionPolicy Bypass -File .\scripts\publish-installer.ps1
```

The publish script downloads pinned external handheld components, verifies their
SHA-256 hashes and creates the installer under `dist\installer`.

Xbox, Epic, GOG, Discord, and other user sign-ins are intentionally not copied.
They are account- or Windows-user-bound and should be signed in again on the new
PC. In particular, Discord tokens are protected with Windows DPAPI and cannot be
ported safely to a different Windows account.
