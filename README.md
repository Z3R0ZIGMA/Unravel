# Unravel
*Everything about this PC.*

A portable Windows app for IT technicians. Run it on a PC to see its hardware, Windows, software and security at a
glance, find what needs fixing, run a few quick tests, and save a report for the ticket or the customer.

**Version 0.5.0.** Phases 1-5 of [plan.md](plan.md) are done; RMM mode (Phase 6) comes next. Code signing comes later.

- [Privacy](#privacy): what Unravel reads and writes. No telemetry.
- [DEVELOPMENT.md](DEVELOPMENT.md): building, testing, releasing and the project layout.
- [TESTING.md](TESTING.md): the release test checklist (Windows versions, makes, VMs, standard user).
- [CHANGELOG.md](CHANGELOG.md): what changed in each version.
- [plan.md](plan.md): the original spec.

## Requirements
- Windows 10 22H2 or Windows 11, x64 or ARM64
- Nothing to install: `Unravel.exe` is a single self-contained file. It runs as a standard user.
- PDF reports use the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and comes with Edge on Windows 10.

## Get it
Download `Unravel.exe` from the [Releases](https://github.com/Z3R0ZIGMA/Unravel/releases) page
(`Unravel-arm64.exe` for Windows on ARM), copy it to a USB stick and double-click it on the PC. Or build it yourself
(see [DEVELOPMENT.md](DEVELOPMENT.md)).

Unravel is not code-signed yet, so Windows may show "Windows protected your PC". If it does, click
**More info > Run anyway**. To check your download, run `Get-FileHash Unravel.exe` in PowerShell and compare the
result with `SHA256SUMS.txt` on the release.

## Use it
Unravel scans as soon as it opens (about 5-10 seconds); each section fills in as it is read.

- **Summary:** make, model, serial number, the key specs (processor, memory, storage, Windows, uptime), a health
  badge for each area and the problems found.
- **Device:** identity, name and domain, motherboard and BIOS, and a **Check warranty** button for Dell, HP, Lenovo,
  ASUS, Acer, MSI and Surface.
- **Hardware:** processor, memory modules and upgrade room, disks and volumes, graphics and displays, network adapters
  and Wi-Fi, battery (with **Open in Unveil** for the full battery check).
- **OS:** Windows edition, version and support status, activation, pending restart, startup programs, services and
  problem drivers.
- **Software:** installed programs and Store apps with search, Microsoft 365 Apps (channel, bitness, signed-in
  account), remote access tools and duplicate versions.
- **Security:** antivirus, firewall, BitLocker, Secure Boot and TPM, User Account Control, Entra / domain join and
  Intune enrolment, and local administrators.
- **Diagnostics:** every health check with what to do, Windows 11 readiness, Windows support and updates, and
  stability over 30 days (blue screens, unexpected shutdowns, disk and hardware errors, app crashes, the Reliability
  Monitor score). Hover a status icon to see what the value means and what is good.
- **Active tests** run only when you click **Run**, and each says first what it will do: disk speed, network (pings
  and DNS), CPU stress (with clock speed under load), system files (`sfc /scannow` and DISM, administrator) and
  Windows Memory Diagnostic (runs at the next restart).
- **Reports:** add the organisation, user and your notes, then save an **HTML report** (one file, prints cleanly),
  a **PDF**, the **JSON data** (everything read, to compare the PC later) or the **software list** as CSV. Active
  tests you ran are included. **Redact sensitive fields** hides serial numbers, names, addresses and IDs for
  reports that go to a third party.
- **Restart as administrator** (one UAC prompt) adds BitLocker, TPM details, disk health (SMART), crash dumps and
  the system file test.

Unravel never uses `Win32_Product` (which makes Windows Installer check every program), and a section that cannot be
read shows "Couldn't read X (reason)" instead of stopping the scan.

## Privacy
Unravel is a diagnostic tool that a technician runs on a PC. It reads information about the PC, shows it on screen,
and writes files only where the technician asks it to.

### The short version
- **No telemetry.** No analytics, crash reporting, update checks, accounts or cloud features. Nothing is sent to the
  developer or anyone else.
- **Network use only on request:** the **Network** test pings your gateway, 1.1.1.1 (Cloudflare) and 8.8.8.8 (Google)
  and looks up three well-known names. **Check warranty** opens the maker's website in your browser (Dell and
  Lenovo pages are opened with the serial number in the address; the others have it copied to the clipboard to
  paste). **Open in Unveil** opens Unveil's releases page if `Unveil.exe` is not next to `Unravel.exe`.
- **Reading, not changing:** the scan changes nothing on the PC. Two tests do change things, only when clicked and
  after saying so: `sfc /scannow` repairs damaged Windows files, and Windows Memory Diagnostic schedules a test at
  the next restart.
- **Administrator access is optional.** Unravel runs as a standard user and asks for administrator rights (a UAC
  prompt) only when the technician clicks **Restart as administrator**.

### What Unravel reads
All on the PC itself, with Windows' own APIs and tools: WMI/CIM, the registry, the event logs, the Windows Update
history, the Store app list, scheduled tasks, and the output of `dsregcmd`, `netsh` and `powercfg`. This includes
serial numbers, the computer, domain and tenant names, user and administrator account names, the Microsoft 365
account, MAC and IP addresses and the Wi-Fi network name.

### What Unravel writes, and where
- **Reports:** HTML, PDF, JSON and CSV files, saved only where the technician chooses.
- **Clipboard:** the copy buttons next to serials, MAC addresses and keys, when clicked.
- **Battery:** `powercfg /batteryreport` writes to a temporary file in `%TEMP%`, deleted as soon as it is read.
- **Disk speed test:** a temporary file in `%TEMP%` (1 GB by default), deleted when the test ends.
- **PDF export:** the WebView2 runtime keeps its browser profile in `%TEMP%\Unravel\WebView2`.
- Nothing else: no settings file, no log, nothing next to `Unravel.exe`.

### What the reports contain
Reports describe the PC and contain device identifiers: serial numbers, the computer and user names, the domain or
tenant, IP and MAC addresses and the Microsoft 365 account. Treat them as client data. With **Redact sensitive
fields** on, all four exports replace those with "[redacted]" (also where they appear inside paths and messages) and
the file name uses the model instead of the computer name and serial number. The organisation, user and notes are
kept as typed.

## Licence
MIT: see [LICENSE](LICENSE).
