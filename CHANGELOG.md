# Changelog

All notable changes to Azure VM Script Runner. Versions follow SemVer; each
release is tagged `vX.Y.Z` and published on GitHub Releases as a portable zip.

## [1.0.3] — 2026-07-04

### Added

- **Parallel batches for scheduled executions** — the runbook now starts VMs
  asynchronously in configurable waves (new *Batch size* field on the Schedule
  page, default 5) instead of one at a time; 25 ten-minute installs at batch
  size 5 now take ~50 minutes instead of ~4 hours
- Per-VM timeout field on the Schedule page
- Clear button on the Schedule page log

### Changed

- Much richer scheduled-run output: timestamps on every line, batch headers,
  per-VM start/completion with durations, download source URL and package size,
  PSADT version detected, install duration, and an end-of-run summary table
  (per-VM status + totals)
- The in-guest "managed identity download unavailable (400)" message now
  explains it simply means the VM has no identity and the next method is tried
  (it was expected behaviour that read like an error)

## [1.0.2] — 2026-07-04

### Added

- Disconnect button in the connection strip — switch accounts without restarting
- Select-all checkbox in the VM grid header (respects active filters)
- Clear Output button on the Run and Deploy pages
- Project icon and screenshot placeholders in the README

### Changed

- Schedule page task sources refresh live when tasks change or the tab is
  opened (previously required an app restart)
- Removed duplicated username from the header
- Documentation overhauled: real-world scenarios, permissions table, scheduling
  resource reference, logging locations, build-from-source guide
- Distribution simplified to the portable zip only (winget manifests and MSIX
  packaging removed)

## [1.0.1] — 2026-07-04

First public release.

- VM discovery via Azure Resource Graph with multi-select filters and search
- PowerShell/CMD execution at scale via Managed Run Commands (parallel waves,
  retries, timeouts, cancellation, per-VM results)
- PSADT v3/v4 deployments from private blob storage (managed identity / auto
  SAS, entry-point auto-detection, exit-code classification)
- Reusable task library with 8 built-ins and PowerShell/CMD/PSADT task editor
- Scheduling via Azure Automation (single generic runbook, Azure-parity
  recurrence, BYO managed identity, environment adoption with deep validation)
- Local execution history (SQLite) with full script/output/error detail
- Safety rails: target confirmation, mass-operation warnings, preflight checks
