# Packaging

One distribution channel: the **portable single-file exe**, published as a ZIP on
GitHub Releases.

```powershell
./Package-Portable.ps1 -Version 1.0.2
```

This publishes `AzureVmScriptRunner.exe` self-contained for win-x64 (no .NET
installation needed on target machines, single file), zips it to
`packaging/output/`, and writes a `.sha256` file with hashes for the release
notes.

## Release flow

```powershell
./Package-Portable.ps1 -Version 1.0.2
git add -A && git commit -m "Release 1.0.2" && git push
gh release create v1.0.2 `
    packaging/output/AzureVmScriptRunner_v1.0.2_win-x64.zip `
    packaging/output/AzureVmScriptRunner_v1.0.2_win-x64.zip.sha256 `
    --title "v1.0.2" --notes "What changed..."
```

The `-Version` flows into the exe, the app's title bar and footer, so the
release tag and what users see always agree.

## Signing

Releases are **unsigned** by choice (no paid certificate). Users may see one
Windows SmartScreen "unrecognised app" prompt on first manual run; SHA256 hashes
are published with every release for verification. If the project ever wants
signing, SignPath.io offers free code signing for open-source projects.
