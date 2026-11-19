# Unravel — Project Plan

> A portable Windows desktop app that unravels everything about a computer: what it is, what's inside it, what it's running, and whether anything is wrong with it.
> Sibling to **Unbound** (OST → PST) and **Unveil** (battery diagnostics). Built in **C# / .NET** like Unveil, with the same dark-mode look.

---

## 1. Goals

- **One click, full picture.** Open the app on a client machine and within ~10 seconds see device identity, hardware, OS, software and health.
- **Portable.** Single `.exe`, no installer, runs from a USB stick or a network share. Leaves nothing behind.
- **Client-ready reports.** Export a clean report (HTML / PDF) to hand to a client or attach to a ticket, plus JSON/CSV for records.
- **Technician-focused diagnostics.** Highlight problems (failing disk, low space, missing updates, Win11 blockers) instead of just dumping data.
- **Works without admin, better with admin.** Collect everything possible as a standard user; clearly flag sections that need elevation and offer "Restart as administrator".

### Non-goals (v1)
- macOS / Linux support.
- Remote scanning of other machines over the network.
- Fixing problems automatically (v1 detects and recommends; a few safe actions come in a later phase).

---

## 2. Tech stack

| Layer | Choice | Why |
|---|---|---|
| Language / runtime | C# on .NET (current LTS) | Same as Unveil; WMI and the registry are native to .NET, so no PowerShell overhead |
| GUI | Same UI framework as Unveil (WPF assumed), dark theme | Consistent family look; reuse Unveil's styles/resources |
| Data collection | `System.Management` (WMI/CIM) for hardware and OS; `Microsoft.Win32.Registry` for software and OS details; `Windows.Management.Deployment.PackageManager` for Store apps; external tools (`dsregcmd`, `netsh`) run as processes and parsed | Direct, fast queries; no dependency on PowerShell execution policy |
| Reports | HTML from a template (e.g. Scriban or embedded HTML + string replacement); PDF via WebView2 `PrintToPdfAsync` | No paid PDF library needed |
| Packaging | `dotnet publish` self-contained, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`; app manifest `requestedExecutionLevel = asInvoker` | Portable single `.exe`; runs on machines without .NET installed; admin optional |
| Tests | xUnit with recorded JSON fixtures from real machines | Parsing logic testable on any machine |

**Rule for Claude Code:** never use `Win32_Product` to list software. It's slow and triggers MSI self-repair on every installed package. Use the registry instead (see §4.5).

---

## 3. Architecture

```
Unravel.sln
├── src/
│   ├── Unravel.App/                 # WPF app: windows, pages, view models, theme
│   │   ├── Views/                   # Summary, Device, Hardware, OS, Software, Security, Diagnostics, Reports
│   │   ├── ViewModels/
│   │   ├── Controls/                # cards, health badges, copyable fields, searchable grid
│   │   ├── Themes/Dark.xaml         # shared palette with Unveil/Unbound
│   │   └── app.manifest             # asInvoker
│   ├── Unravel.Core/                # models + collectors + diagnostics (no UI references)
│   │   ├── Models/                  # DeviceInfo, CpuInfo, MemoryModule, DiskInfo, ...
│   │   ├── Collectors/
│   │   │   ├── DeviceCollector.cs
│   │   │   ├── CpuCollector.cs
│   │   │   ├── MemoryCollector.cs
│   │   │   ├── StorageCollector.cs
│   │   │   ├── GpuDisplayCollector.cs
│   │   │   ├── FirmwareCollector.cs     # motherboard, BIOS, TPM, Secure Boot
│   │   │   ├── NetworkCollector.cs
│   │   │   ├── OsCollector.cs
│   │   │   ├── SecurityCollector.cs
│   │   │   ├── IdentityCollector.cs     # Entra/AD/Intune join state
│   │   │   ├── SoftwareCollector.cs
│   │   │   ├── StartupServicesCollector.cs
│   │   │   ├── DriverCollector.cs
│   │   │   └── BatteryCollector.cs      # summary only; "Open in Unveil" for deep dive
│   │   ├── Diagnostics/
│   │   │   ├── HealthRules.cs           # rules → findings (OK / Warning / Critical)
│   │   │   ├── Win11Readiness.cs
│   │   │   ├── EventLogAnalyzer.cs
│   │   │   └── ActiveTests.cs           # on-demand tests (disk speed, network, etc.)
│   │   ├── Infrastructure/
│   │   │   ├── Wmi.cs                   # query helper with timeouts + null-safe getters
│   │   │   ├── ProcessRunner.cs         # run dsregcmd/netsh/sfc, capture output
│   │   │   ├── Elevation.cs             # IsAdmin(), RelaunchAsAdmin()
│   │   │   └── ScanRunner.cs            # runs collectors in parallel, progress events
│   │   └── Reports/
│   │       ├── Templates/report.html
│   │       ├── HtmlReport.cs
│   │       ├── PdfReport.cs
│   │       └── DataExport.cs            # JSON + CSV
│   └── Unravel.Cli/                 # optional: headless entry point (see §12)
└── tests/
    └── Unravel.Core.Tests/
        └── Fixtures/                # captured JSON from real machines (desktop, laptop, VM, Surface)
```

**Collector contract** — every collector implements:

```csharp
public interface ICollector
{
    string Section { get; }
    Task<CollectorResult> CollectAsync(CollectContext ctx, CancellationToken ct);
}

// CollectorResult: Data (section model), Warnings (List<string>), NeedsAdmin (bool), Duration (TimeSpan)
```

A collector that fails must never crash the app: it returns partial data and a warning, and the UI shows "Couldn't read X (reason)".

---

## 4. Features

### 4.1 Summary page (the landing screen)
- Big header: **Make + Model**, computer name, serial number (one-click copy).
- Key specs in one row: CPU · RAM · Storage · OS · Uptime.
- **Health strip:** traffic-light badges for Storage, Memory, Updates, Security, Win11 readiness, Battery.
- Top findings list ("Disk C: is 94% full", "SMART predicts failure on Disk 1", "Pending reboot for 12 days").
- Buttons: **Export report**, **Copy summary** (plain text for pasting into a ticket), **Run diagnostics**.

### 4.2 Device
| Field | Source |
|---|---|
| Manufacturer, Model | `Win32_ComputerSystem` (`Manufacturer`, `Model`), `Win32_ComputerSystemProduct` (`Name`, `Version` — Lenovo puts the friendly name in `Version`) |
| Serial number / Service Tag | `Win32_BIOS.SerialNumber` (fallback `Win32_SystemEnclosure.SerialNumber`) |
| SKU / product number | `Win32_ComputerSystem.SystemSKUNumber` (useful for HP/Lenovo part lookups) |
| Chassis type | `Win32_SystemEnclosure.ChassisTypes` → Laptop / Desktop / Mini PC / All-in-one / VM |
| UUID | `Win32_ComputerSystemProduct.UUID` (handy for Autopilot/Intune matching) |
| Computer name, domain/workgroup | `Win32_ComputerSystem` |
| **Warranty lookup** | Button that opens the vendor's warranty page with the serial pre-filled where the vendor allows (Dell, HP, Lenovo); otherwise copies serial and opens the support page |
| VM detection | Model/manufacturer contains VMware / Hyper-V / VirtualBox / QEMU |

### 4.3 Hardware

**Processor** — `Win32_Processor`
- Name, manufacturer, cores / logical processors, base clock (`MaxClockSpeed`), current clock, L2/L3 cache, socket, architecture, virtualisation enabled (`VirtualizationFirmwareEnabled`).
- Generation hint parsed from the name (e.g. "Intel Core i5-1235U → 12th gen"), used for Win11 readiness.

**Memory** — `Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray`
- Total installed and usable.
- Per module: slot (`DeviceLocator`), size, rated speed (`Speed`), running speed (`ConfiguredClockSpeed`), manufacturer, part number, form factor (DIMM / SODIMM / soldered).
- **Generation** from `SMBIOSMemoryType`: 24 = DDR3, 26 = DDR4, 34 = DDR5, 29 = LPDDR3, 30 = LPDDR4, 35 = LPDDR5 (fallback: infer from part number, else "Unknown").
- **Upgrade info:** total slots, slots used/free, max supported capacity (`Win32_PhysicalMemoryArray.MaxCapacityEx`) — e.g. "1 of 2 slots free, supports up to 64 GB".
- Flag when running speed < rated speed, or when modules are mismatched (single-channel warning).

**Storage** — `MSFT_PhysicalDisk`, `MSFT_Disk` (namespace `root\Microsoft\Windows\Storage`), `Win32_LogicalDisk`, `MSFT_StorageReliabilityCounter`
- Per physical disk: model, serial, media type (`MediaType` 3 = HDD, 4 = SSD), bus (`BusType` 17 = NVMe, 11 = SATA, 7 = USB), size, health status, partition style (GPT/MBR).
- Per volume: letter, label, file system, size, free space, % used (bar), BitLocker status.
- SMART-style data where available (admin): temperature, wear %, power-on hours, read/write errors.

**Graphics & displays** — `Win32_VideoController`, `WmiMonitorID` (namespace `root\wmi`)
- GPU name, VRAM, driver version and date.
- Connected monitors: make, model, serial (from EDID), resolution.

**Motherboard & firmware** — `Win32_BaseBoard`, `Win32_BIOS`
- Board make/model, BIOS vendor/version/date, UEFI vs Legacy boot, Secure Boot on/off, TPM present/version/enabled (`Win32_Tpm`, admin).
- Flag BIOS older than ~3 years.

**Network adapters** — `MSFT_NetAdapter`, `System.Net.NetworkInformation`
- Name, type (Ethernet / Wi-Fi), MAC, link speed, IP, gateway, DNS, DHCP on/off.
- Wi-Fi: SSID, signal, band/channel, radio type (`netsh wlan show interfaces`) — useful when checking against UniFi AP coverage.

**Battery (laptops)** — short summary (design vs full charge capacity, health %, cycle count) with an **"Open in Unveil"** button for the deep dive. Since both apps are C#, consider moving Unveil's battery reading code into a shared class library later instead of duplicating it.

### 4.4 Operating system — `Win32_OperatingSystem`, registry `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`
- Edition (Home / Pro / Enterprise / Education), display version (e.g. 24H2), build + UBR (e.g. 26100.2314), architecture.
- Install date, last boot time, uptime.
- Activation status and channel (Retail / OEM / Volume / Digital) via `SoftwareLicensingProduct`.
- System locale, time zone, language.
- **Windows Update:** last installed update + date, pending reboot (check the standard registry keys), update service state.
- **End-of-support check:** compare build against a bundled lifecycle table (updateable JSON) → "Supported until …" / "Out of support".

### 4.5 Software catalogue
- Sources:
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (read with `RegistryView.Registry64`)
  - `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall` (or `RegistryView.Registry32`)
  - `HKCU\...\Uninstall` (per-user installs like Teams, Zoom, Chrome)
  - Store/AppX apps (`PackageManager`, optional toggle — hide framework/system packages by default)
- Columns: Name, Version, Publisher, Install date, Size, Architecture (32/64), Scope (Machine/User), Uninstall string (hidden column).
- Searchable, sortable, filterable grid; hide system components/updates by default (`SystemComponent = 1`, `ParentKeyName`).
- **Highlights:**
  - Microsoft 365 Apps: version, channel (Current / Monthly Enterprise / Semi-Annual), 32 vs 64-bit, licensed account if readable.
  - Multiple antivirus products installed (conflict warning).
  - Known remote-access tools (AnyDesk, TeamViewer, etc.) listed in a separate "Remote access" group — useful for security checks.
  - Duplicate/older versions of the same product.
- Export to CSV.

### 4.6 Security & management (high value for M365 work)
- **Defender / AV:** product(s) from `root\SecurityCenter2\AntiVirusProduct`, real-time protection on, signature age (`MSFT_MpComputerStatus` in `root\Microsoft\Windows\Defender`).
- **Firewall:** profile states (Domain / Private / Public).
- **BitLocker:** per-volume protection and encryption state (`Win32_EncryptableVolume`, admin); whether a recovery key is backed up to Entra (where detectable).
- **Device join state** (`dsregcmd /status` parsed): Entra joined / Hybrid joined / Domain joined / Workgroup, tenant name & ID, Intune MDM enrolled, PRT present.
- **Local admins:** members of the local Administrators group.
- **UAC** level, **Secure Boot**, **TPM** (cross-link with Firmware section).

### 4.7 Startup, services & drivers
- Startup programs (registry Run keys, Startup folders, scheduled tasks at logon) with publisher and enabled state.
- Services: non-Microsoft services, and any set to Automatic but not running.
- Drivers: devices with problems (`Win32_PnPEntity` where `ConfigManagerErrorCode ≠ 0`) — the "yellow exclamation" list from Device Manager. Optionally, the oldest third-party drivers.

---

## 5. Diagnostics

### 5.1 Passive health checks (run automatically on every scan)
Each rule produces a finding with severity, a plain-English message, and a recommended action.

| Area | Check | Severity logic |
|---|---|---|
| Storage | Free space on system drive | < 15% Warning, < 5% Critical |
| Storage | Disk health status / SMART predicted failure | Any not Healthy → Critical |
| Storage | HDD as system drive | Info: "SSD upgrade recommended" |
| Storage | SSD wear | > 80% used → Warning |
| Memory | Total RAM | < 8 GB Warning (with upgrade path from §4.3) |
| Memory | Running speed < rated / single channel | Info |
| OS | Out-of-support build | Critical |
| OS | Not activated | Warning |
| Updates | Pending reboot > 7 days, last update > 45 days | Warning |
| Security | AV off / signatures > 7 days / no AV | Critical |
| Security | BitLocker off on a laptop | Warning |
| Security | Firewall profile off | Warning |
| Firmware | BIOS > 3 years old, Secure Boot off | Info / Warning |
| Drivers | Devices with errors | Warning |
| Stability | BSODs, unexpected shutdowns, app crashes in last 30 days (see 5.2) | Warning if repeated |
| Uptime | > 14 days | Info |

### 5.2 Event log & stability
- Read with `System.Diagnostics.Eventing.Reader.EventLogReader` using XPath queries. Count and list in the last 7/30 days:
  - Bugchecks / BSOD (System log, Event ID 1001 `BugCheck`), plus minidump files in `C:\Windows\Minidump`.
  - Unexpected shutdowns (Kernel-Power 41, EventLog 6008).
  - Disk errors (Event IDs 7, 51, 153 from disk/storport).
  - WHEA hardware errors (WHEA-Logger) — often RAM or CPU.
  - Application crashes (Application Error 1000) grouped by app.
- Windows Reliability Monitor score history (`Win32_ReliabilityStabilityMetrics`) as a small chart.

### 5.3 Windows 11 readiness
- Check CPU against a bundled supported-CPU list, TPM 2.0, Secure Boot capable, UEFI, ≥ 4 GB RAM, ≥ 64 GB storage, GPT system disk.
- Result: Ready / Not ready, with the specific blocker(s).

### 5.4 Active tests (on demand, user clicks "Run")
- **Disk speed test:** sequential read/write on a temp file (default 1 GB, configurable, `FileOptions.WriteThrough` to bypass cache), result compared to typical HDD / SATA SSD / NVMe ranges.
- **Network test:** ping gateway, ping 1.1.1.1/8.8.8.8, DNS resolution time, packet loss (no download speed test: one download from one server is not a reliable measure).
- **Memory test:** schedule Windows Memory Diagnostic (`mdsched.exe`) on next reboot, and read back the previous result from the event log.
- **System file check:** run `sfc /scannow` and `DISM /Online /Cleanup-Image /CheckHealth` (admin), stream output to the UI.
- **CPU stress (short):** 30–60 s load to check for throttling/crashes; show clock speed during the run. (Temperatures are not reliably exposed on Windows without kernel drivers — show only if `MSAcpi_ThermalZoneTemperature` returns sensible values, otherwise mark as "not available".)

---

## 6. Reports & export
- **HTML report** (single self-contained file, print-friendly) — Summary, all sections, findings, and the technician's notes.
- **PDF** of the same report.
- **JSON** (full raw data, versioned schema, `System.Text.Json`) — lets you compare the same machine over time.
- **CSV** of the software catalogue.
- **Technician notes** field and optional **client name / ticket number** included on the report.
- Default file name: `Unravel_<ComputerName>_<Serial>_<YYYY-MM-DD>.html`.
- **Compare two scans (later phase):** load two JSON files and highlight what changed (new software, RAM added, disk replaced, etc.).

---

## 7. UI / UX
- Dark mode, same palette and fonts as Unveil/Unbound; left sidebar: **Summary · Device · Hardware · OS · Software · Security · Diagnostics · Reports**.
- Scan runs on start with a progress bar per section; sections fill in as their collectors finish (don't block on the slowest one).
- Every value is selectable; serials, MAC addresses and keys get a copy icon.
- Findings use consistent badges: ✅ OK · ⚠️ Warning · ⛔ Critical · ℹ️ Info.
- "Needs admin" banner on sections with incomplete data, with a **Restart as administrator** button.
- Refresh button per section and global rescan.
- About dialog with version and GitHub link; check for updates via GitHub Releases (optional, off by default).

---

## 8. Privacy & safety
- Read-only by default. Nothing is sent anywhere; no telemetry.
- Active tests and sfc/DISM require an explicit click and show what they'll do first.
- Reports may contain serials, IPs, usernames and local admin names — add a **"Redact sensitive fields"** toggle on export for reports going to third parties.

---

## 9. Build phases

**Phase 1 — Foundation**
- Solution scaffold (App, Core, Tests), dark theme from Unveil, collector framework, WMI helper, elevation helper.
- Collectors: Device, CPU, Memory, Storage (basic), OS.
- Summary page.
- ✅ Done when: running the app on your own PC shows correct make/model/serial, CPU, RAM (incl. DDR generation), disks with free space, and Windows version/build.

**Phase 2 — Full inventory**
- Software catalogue (registry + Store apps), GPU/displays, motherboard/BIOS/TPM/Secure Boot, network, battery summary.
- Security & management section (Defender, firewall, BitLocker, dsregcmd join state, local admins).
- Startup, services, problem drivers.

**Phase 3 — Diagnostics**
- Rules engine + all passive checks, event log & stability, Win11 readiness.
- Active tests: disk speed, network, sfc/DISM, memory diagnostic scheduling.

**Phase 4 — Reports**
- HTML/PDF/JSON/CSV export, technician notes, redaction toggle.

**Phase 5 — Packaging & release**
- Self-contained single-file publish, app icon, version info, code-signing (optional).
- Test on: Win10 22H2, Win11 23H2/24H2/25H2, a Dell/HP/Lenovo laptop, a desktop, a Hyper-V/VMware VM, a standard user account (no admin).
- GitHub release with changelog.

**Phase 6 — RMM mode** (see §12)

**Later ideas**
- Scan comparison (before/after a repair or upgrade).
- Safe one-click fixes: clear temp files, restart Windows Update service, flush DNS.
- Remote scan of another PC on the network (WinRM).
- Shared class library with Unveil for battery data and common UI styles.

---

## 10. Notes for Claude Code
- Start with Phase 1 only; confirm output on a real machine before moving on.
- Keep `Unravel.Core` free of UI references so the CLI/RMM mode can reuse it.
- Run collectors in parallel with a per-collector timeout (default 20 s) so one hung WMI query can't freeze the scan. Set `EnumerationOptions.Timeout` / `ReturnImmediately` on WMI queries.
- Dispose `ManagementObject`/`ManagementObjectSearcher` instances (`using`) — WMI objects leak if not disposed.
- Handle missing/blank values gracefully — OEMs often put "To be filled by O.E.M.", "Default string" or "System Serial Number" in fields; treat these as "Not set".
- Save a JSON fixture from every test machine into `tests/Unravel.Core.Tests/Fixtures/` and write parser tests against them.
- Before publishing, check the name "Unravel" isn't already used by a similar Windows tool (as happened with PowerProbe).

---

## 11. Reference
- Look at the Unveil repo for: theme/styles, project layout, publish settings, and the RMM approach — match them where it makes sense.

---

## 12. RMM mode (last phase)

> **To confirm before building:** mirror exactly how Unveil's RMM run works (same switches, output location, exit codes, and how the RMM picks up results). Add Unveil's RMM section here before starting this phase.

Draft until then:
```
Unravel.exe --silent --json C:\Reports\scan.json --html C:\Reports\scan.html
Unravel.exe --silent --software-csv C:\Reports\software.csv
Unravel.exe --silent --sections device,hardware,os --json -
```
- No window when `--silent` is used; reuse `Unravel.Core`, so results match the GUI exactly.
- Exit code reflects worst finding (0 = OK, 1 = Warning, 2 = Critical) so RMM tools can alert on it.
- Writes a short one-line summary to standard output for RMM script logs.
