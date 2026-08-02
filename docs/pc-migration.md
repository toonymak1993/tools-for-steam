# Moving the project to another PC

The GitHub repositories are the migration source of truth. Generated `bin`, `obj`,
`.codex-build`, `artifacts`, and `dist` directories are intentionally not versioned;
the build scripts recreate them.

## 1. Install the development tools

- Git and GitHub CLI (`gh`), then run `gh auth login`.
- .NET SDK 10.0. The last verified development machine used SDK `10.0.302`.
- Inno Setup 6 for the complete installer.
- Windows 10/11 SDK build tools containing `makeappx.exe` and `signtool.exe`.
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

## 3. Restore the Xbox Mode signing assets

The signing certificate contains a private key and must never be committed to
this public repository. An encrypted copy is stored in the separate private
repository `toonymak1993/tools-for-steam-signing-backup`. The recovery key is
kept separately from both GitHub repositories.

```powershell
Set-Location ..
gh repo clone toonymak1993/tools-for-steam-signing-backup
powershell -ExecutionPolicy Bypass `
  -File .\tools-for-steam-signing-backup\Restore-XboxSigning.ps1 `
  -ProjectRoot .\tools-for-steam `
  -RecoveryKey '<separately stored recovery key>'
```

The restore script recreates these ignored files:

- `dist\signing\ToolsForSteam.XboxHost.pfx`
- `dist\signing\ToolsForSteam.XboxHost.password`

If Microsoft later provides a production Gaming Home SCCD, also place it at
`dist\signing\ToolsForSteam.XboxHost.sccd` or set `TFS_XBOX_HOST_SCCD` to its
location. The current build uses the versioned Developer Mode SCCD fallback.

## 4. Verify and publish

```powershell
dotnet test .\SteamLoader.slnx
powershell -ExecutionPolicy Bypass -File .\scripts\publish-installer.ps1
```

The publish script downloads pinned external handheld components, verifies their
SHA-256 hashes, builds the signed Xbox Host MSIX, and creates the installer under
`dist\installer`.

Xbox, Epic, GOG, Discord, and other user sign-ins are intentionally not copied.
They are account- or Windows-user-bound and should be signed in again on the new
PC. In particular, Discord tokens are protected with Windows DPAPI and cannot be
ported safely to a different Windows account.
