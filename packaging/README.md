# Packaging & Distribution

Two channels from the same code:

| Channel | Script | Audience |
|---|---|---|
| **Portable ZIP** (single `AzureVmScriptRunner.exe`, unsigned, free) | `./Package-Portable.ps1 -Version 1.0.1` | Public / open-source: GitHub Releases + winget (`packaging/winget/` templates) |
| **MSIX** (self-signed, prompt-free on managed devices) | `./Package-Msix.ps1 -Version 1.0.1.0 -ExportCer` | Your own Intune estate |

## Portable / open-source channel

`Package-Portable.ps1` publishes a self-contained **single-file exe** (no .NET
install needed), zips it, and writes SHA256 hashes. Manual downloads show one
SmartScreen "unrecognised app → More info → Run anyway" prompt on first launch;
`winget install` verifies the hash and avoids that ceremony. If the project
gains traction, apply to SignPath.io's free open-source signing to remove the
prompt entirely.

Build a signed MSIX:

```powershell
./Package-Msix.ps1 -Version 1.0.1.0 -ExportCer
```

Output lands in `packaging/output/`. The script publishes self-contained win-x64
(no .NET runtime needed on targets), packs with `makeappx` (auto-downloaded from
the `Microsoft.Windows.SDK.BuildTools` NuGet package if the Windows SDK isn't
installed), and signs with a **timestamped** signature.

## Signing without paying for a certificate

MSIX packages must be signed with a certificate the target machine trusts. That
does **not** require a paid certificate in a managed (Intune/AD) environment:

### Recommended: self-signed certificate + Intune (free, zero prompts)

1. `./Package-Msix.ps1 -Version x.y.z.0 -ExportCer`
   — creates/reuses a 5-year self-signed cert (`CN=ModernWorkspaceHub`), signs and
   timestamps the package, and exports `ModernWorkspaceHub-Signing.cer`.
2. In Intune: **Devices → Configuration → New profile → Templates → Trusted
   certificate**, upload the `.cer`, destination store **Local Machine – Trusted
   People** (or Trusted Root), assign to your admin workstations.
3. In Intune: **Apps → Windows → Add → Line-of-business app**, upload the
   `.msix`, assign.

Result: silent, prompt-free installs and upgrades on every managed device.
Because the signature is timestamped, already-installed packages stay valid even
after the certificate expires; new packages after expiry just need the refreshed
`.cer` re-deployed (the script auto-creates a new cert when the old one lapses).

Keep the certificate's private key safe: it lives in the build user's
`Cert:\CurrentUser\My`. Anyone with it can sign packages your fleet trusts.

### Alternatives

| Option | Cost | When |
|---|---|---|
| Azure Trusted Signing | ~$10/month | Distribution beyond machines you manage; publicly trusted, near-zero SmartScreen friction |
| SignPath.io (free OSS tier) / Certum OSS cert (~€70/yr) | Free–cheap | If the tool is published as open source (like Win32Forge) |
| Unsigned MSI/EXE instead of MSIX | Free | **Not better**: unsigned installers still hit SmartScreen "unrecognised app" prompts. Signing is the fix, not the format — and the same free Intune approach works for MSI/EXE anyway |

### Production notes

- The manifest `Publisher` (`packaging/AppxManifest.xml`) must exactly match the
  signing certificate's Subject. If you later adopt an organisational cert,
  update that one line.
- Pass `-CertPath your.pfx -CertPassword (Read-Host -AsSecureString)` to sign
  with a real certificate instead.
- The `-Version` you pass flows into both the package identity and the version
  shown in the app's title bar and footer, so they always agree.
