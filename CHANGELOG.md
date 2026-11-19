# Changelog

## 0.6.1: security fixes
From a security review. **Update the N-sight script** in Script Manager: the old one can be abused (below).
- **N-sight script:** `C:\ProgramData\Unravel` is now usable by SYSTEM and Administrators only. Before, a standard user
  on the PC could create the folder first, or add a file next to `Unravel.exe`, and have it run as SYSTEM, and could read
  the reports. A folder anyone could change is moved aside unopened and replaced. `Unravel.exe` is checked against
  `SHA256SUMS.txt` before every run, also when the download fails; an exe that does not match never runs (1011).
- Startup programs whose path is on another computer (`\\server\share\...`) are no longer opened to read their name:
  that made Windows sign in to that computer, as the PC's own account when run from N-sight.
- **Open in Unveil** from an administrator window starts Unveil without administrator rights.
- PDF export: SmartScreen is off for the hidden page, so the report is never looked up online.
- The N-sight error line no longer shows user folder names or e-mail addresses from an error message.
- Run from N-sight, per-user startup paths such as `%LOCALAPPDATA%\...` point into the signed-in user's profile;
  report names include seconds, so two runs in one minute no longer overwrite each other.
- CSV: values starting with a tab or carriage return are also kept from running as formulas in Excel.
- Repository: automated build and tests on GitHub, Dependabot, `SECURITY.md`, line-ending rules and a pinned SDK.

## 0.6.0: run it from N-able N-sight RMM
- **`Unravel-NSight.ps1`** (attached to the release): upload it in N-sight's Script Manager (timeout 300 seconds) and
  add it to a PC as an Automated Task or a Script Check. It downloads Unravel, checks it against `SHA256SUMS.txt`,
  scans with no window as the system account, saves the HTML and JSON report in `C:\ProgramData\Unravel\Reports\`
  (the newest 30 runs are kept) and shows one line on the dashboard. The check passes when there is nothing worse than
  information and fails with 1001 (warnings) or 1002 (critical). `-ReportOnly`, `-Redact`, `-SoftwareCsv`,
  `-Organisation`, `-Version`, `-NoDownload` and `-Keep` work as described in the README.
- Run as the system account, startup programs, per-user programs, Store apps and the Microsoft 365 account are read
  for the person signed in at the screen.
- New icon, logo and social preview: a blue spiral in the middle of the chip, in place of the thin thread, so it
  stays clear at 16 px in the taskbar.

## 0.5.0: first release
The first public release: Phases 1-5 of [plan.md](plan.md).

**Inventory**
- Summary page: make, model, serial, key specs, a health badge per area and the problems found.
- Device: identity, name and domain, motherboard and BIOS, warranty links for Dell, HP, Lenovo, ASUS, Acer, MSI and
  Surface.
- Hardware: processor (generation), memory modules and upgrade room, disks (SMART as administrator) and volumes,
  graphics and displays, network adapters and Wi-Fi, battery with **Open in Unveil**.
- OS: edition, version, build and support status, activation, pending restart, startup programs (named as Task Manager
  names them), services and problem drivers.
- Software: installed programs and Store apps with search (never `Win32_Product`), Microsoft 365 Apps with the
  signed-in account, remote access tools and duplicate versions.
- Security: antivirus, firewall, BitLocker, Secure Boot, TPM, UAC, Entra / domain join, Intune and local administrators.

**Diagnostics**
- Health checks across every section, each with what to do; hover any status icon for what it means.
- Windows 11 readiness, Windows end-of-support dates (a `lifecycle.json` next to the exe can update them), Windows
  Update history and failures.
- Stability over 30 days: blue screens, unexpected shutdowns, disk and hardware (WHEA) errors, app crashes, crash
  dumps, memory test results and the Reliability Monitor score and chart.
- Active tests, each run only on request: disk speed, network (pings and DNS), CPU stress, `sfc` and DISM, Windows
  Memory Diagnostic.

**Reports**
- HTML report (one self-contained file, dark on screen, white in print) and PDF, both with the logo, the organisation,
  user and notes, every section and the active tests run.
- JSON data in a versioned format, and the software list as CSV.
- **Redact sensitive fields** for reports going to a third party.

**Packaging**
- Single self-contained `Unravel.exe` for x64 and ARM64, with version information; runs as a standard user, with
  **Restart as administrator** for the details that need it.
