# Developing Unravel

## Requirements
- Windows 10 22H2 or Windows 11
- .NET 10 SDK
- Python with PySide6, only to redraw the icon (`assets/make_icon.py`)

## Build, test, run
```
dotnet build Unravel.sln
dotnet test Unravel.sln
dotnet run --project src/Unravel.App
```

## Single-file exe
Publish settings live in `src/Unravel.App/Unravel.App.csproj` (same as Unveil: self-contained, single file, compressed).
```
dotnet publish src/Unravel.App -c Release -r win-x64 -o dist/win-x64
```
Use `-r win-arm64` for ARM laptops. The exe runs as the current user (`asInvoker`); "Restart as administrator" elevates.

## Icon, logo and social preview
The icon is a grey faceted CPU chip with a blue thread unwinding from it, in the same flat faceted style, greys and
accent blue as the Unbound and Unveil icons. `assets/make_icon.py` draws everything from code:
```
"..\Unbound\.venv\Scripts\python" assets\make_icon.py
```
It writes `assets/unravel.ico` (16-256 px), `assets/unravel.png` (256 px logo) and `assets/unravel-1280x640.png`, and
copies the icon and logo into `src/Unravel.App` (exe icon, window icon and the sidebar logo).

GitHub has no API for the social preview: upload `assets/unravel-1280x640.png` by hand in the repository's
Settings > General > Social preview.

## Layout
- `src/Unravel.Core`: models, collectors, health checks and page models. No UI references, so a CLI/RMM mode can reuse it.
  - `Infrastructure/`: `Wmi` (timeouts, disposes every WMI object, refuses `Win32_Product`), `ISystemSource` (live or
    fixture: WMI, registry, tool output such as `dsregcmd`/`netsh`, Store apps, scheduled tasks, file versions),
    `ScanRunner` (collectors in parallel, 20 s timeout each), `ProcessRunner` (System32 tools only, with a timeout),
    `Elevation`, `OemValues` (OEM placeholders such as "To be filled by O.E.M." become "Not set").
  - `Collectors/`: one per section. A collector never throws to the app: failures become "Couldn't read X (reason)".
  - `Summary/` and `Pages/`: what each page shows, as data (cards of fields, tables and status lines).
- `src/Unravel.App`: WPF app. `Themes/Dark.xaml` is Unveil's theme plus a few Unravel styles; `Views/CardTemplates.xaml`
  draws every card; `Views/PageView` draws the Device, Hardware, OS and Security pages.
- `tests/Unravel.Core.Tests`: xUnit. `Fixtures/` holds system data replayed through `FixtureSystemSource`:
  a scrubbed real ASUS laptop (Phase 1 data) and synthetic machines. `FixtureScripts/make_dell_fixture.py` writes the
  synthetic Phase 2 machine.

The target framework is `net10.0-windows10.0.19041.0` because the Store app list uses the Windows Runtime
`PackageManager` API. Unravel runs on Windows 10 1809 and later.

## Developer switches
| Switch | What it does |
|---|---|
| `--capture-fixture <file.json>` | Scans with no window and saves everything read as a fixture. |
| `--fixture <file.json>` | Shows a captured machine instead of this PC. |
| `--dump <file.json>` | Scans with no window and writes every section's parsed data and warnings (add `--fixture` to dump a fixture). |
| `--screenshot <file.png>` | Renders the page to an image after the scan, then exits. `--page Hardware` picks the page; `--screenshot-window` also saves the whole window; `--run-tests` runs the disk (256 MB), network and CPU (30 s) tests through the UI first. |
| `--active-test disk\|network\|cpu <file.json>` | Runs one active test with no window and writes its result (`cpu` runs 30 s). |
| `--export <file.html\|.pdf\|.json\|.csv>` | Scans with no window and writes that export (the type comes from the extension). Add `--redact`, `--organization <name>`, `--user <name>`, `--notes <text>`, or `--fixture <file.json>` to report on a captured machine. |

## Reports
- All four exports are rendered from one `ReportDocument` (`src/Unravel.Core/Reports`), so they agree. With
  redaction on, the scan is copied through JSON with the sensitive properties replaced and the same values removed
  from all other text (see `Redactor`); `Phase4Tests` checks every fixture for leaks.
- PDF uses the Edge WebView2 runtime (part of Windows 11; installed with Edge on Windows 10) to print the HTML
  report with its print styles. Its browser profile goes to `%TEMP%\Unravel\WebView2`. Without the runtime the PDF
  button says so, and the HTML report still works.

## Diagnostics data
- **End-of-support dates** come from `src/Unravel.Core/Diagnostics/lifecycle.json`, embedded in the exe. A
  `lifecycle.json` in the same shape next to `Unravel.exe` replaces it, so a new Windows release can be added on a
  technician's USB stick without a new build. Unravel never creates or changes that file.
- **Windows 11 readiness** checks the processor by family and generation (Intel 8th gen Core and later plus the
  supported Pentium/Celeron/N-series and Xeon models, AMD Ryzen 2000 Zen+ and later, Snapdragon 850 and later) rather
  than Microsoft's full model list; anything it cannot place shows as unknown. See `Diagnostics/Win11Readiness.cs`.
- **Event log** queries (`Collectors/StabilityCollector.cs`) are XPath over the System and Application logs for the
  last 30 days and work as a standard user; minidumps and SMART data need administrator rights.

Captured fixtures contain serial numbers, UUIDs, MAC and IP addresses, Wi-Fi names and user names: never commit a raw
capture. Keep them in the git-ignored `scratch/` folder, and add scrubbed or synthetic fixtures to `tests/.../Fixtures/`.
