# Changelog

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
