# Azure VM Script Runner

Execute scripts, commands & PSADT deployments on Azure Windows VMs — no RDP required.

Azure VM Script Runner is a free, open-source WPF desktop tool for administering
Azure **Windows** virtual machines without ever opening an RDP session. It uses
Azure-native services throughout: Resource Graph for discovery, Managed Run
Commands for execution, Azure Automation for scheduling, and Entra ID for
sign-in.

> **No warranty.** Provided free of charge, as-is, without warranty of any kind.
> Built and maintained by [modernworkspacehub.com](https://modernworkspacehub.com)

## Features

- **VM discovery** — every Windows VM across all your subscriptions in about a
  second (Azure Resource Graph), with Azure-portal-style multi-select filters
  (subscription, resource group, region, power state) and free-text search
- **Run** — PowerShell or CMD against one or many VMs at once, with per-VM
  progress, output, exit codes, staggered parallelism, timeout and cancellation
- **Deploy (PSADT)** — deploy [PSAppDeployToolkit](https://psappdeploytoolkit.com/)
  (v3 & v4) packages straight from private Azure blob storage: automatic
  download auth (managed identity → short-lived SAS), extract, execute, exit
  code classification (3010 = "reboot required", etc.)
- **Tasks** — an editable library of reusable tasks (GP update, flush DNS,
  service restarts, disk space report…) plus your own PowerShell/CMD/PSADT tasks
- **Schedule** — recurring executions via Azure Automation: one generic runbook,
  parameter-driven schedules (once/hourly/daily/weekly with day picks/monthly
  with month-day or "second Tuesday" recurrence, time zones, expiry). The app
  provisions the infrastructure for you and can adopt an environment a
  colleague already created
- **History** — full local execution history: who ran what, where, when, output,
  errors, exit codes
- **Safety** — explicit target confirmation listing every VM, mass-operation
  warnings, power-state and guest-agent preflight checks
- **Security** — interactive Entra ID sign-in, Azure RBAC, no stored
  credentials, no SAS keys saved anywhere; the Azure Activity Log remains your
  authoritative audit trail

## Install

**winget** (recommended):

```
winget install ModernWorkspaceHub.AzureVmScriptRunner
```

**Portable**: download the latest `AzureVmScriptRunner_vX.Y.Z_win-x64.zip` from
[Releases](../../releases), verify the SHA256 from the release notes, unzip and
run `AzureVmScriptRunner.exe`. Self-contained — no .NET installation required.
Windows SmartScreen may show an "unrecognised app" prompt on first manual run
(the package is unsigned; hashes are published with every release).

## Prerequisites

In-app, the **Prep** page walks through everything. In short, your account needs:

- `Reader` on target subscriptions (discovery)
- `Virtual Machine Contributor` (or equivalent custom role) on target VMs (execution)
- `Storage Blob Data Reader` on your package storage account (PSADT deployments —
  note this is a data-plane role; Owner/Contributor alone is not enough)

## Build from source

```powershell
dotnet build            # .NET 8 SDK required
dotnet test
dotnet run --project src/AzureVmScriptRunner.UI
```

Packaging (portable zip / MSIX): see [packaging/README.md](packaging/README.md).

## Architecture

Clean architecture: `Domain` (no Azure references) → `Application`
(orchestration, provider abstractions) → `Infrastructure` (Azure SDKs, SQLite,
Automation REST) → `UI` (WPF, MVVM). A CLI harness (`tools/`) drives the same
execution core headlessly.

## License

[MIT](LICENSE)
