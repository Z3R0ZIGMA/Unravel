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

## Releasing
1. Set `<Version>` in `Directory.Build.props` and add the version to `CHANGELOG.md` (and the README's version line).
2. Build: `powershell -ExecutionPolicy Bypass -File build\publish.ps1`. It runs the tests, then writes
   `dist\Unravel-<version>\` with `win-x64\` and `win-arm64\` (each `Unravel.exe` and `README.txt`) and `github\`
   (`Unravel.exe`, `Unravel-arm64.exe`, `Unravel-NSight.ps1` and `SHA256SUMS.txt`). The N-sight script downloads
   these by name, so never rename them.
3. Run through [TESTING.md](TESTING.md) with the built exe.
4. Commit, tag `v<version>`, push, and create the GitHub release from the tag with the four files in
   `dist\Unravel-<version>\github\` and the changelog entry as the notes, for example
   `gh release create v0.6.0 dist\Unravel-0.6.0\github\* --title "Unravel 0.6.0" --notes-file notes.md`.

The exes are not code-signed yet, so SmartScreen warns on first run; the release lists SHA-256 hashes instead. The
repository must stay public: the N-sight script downloads from its releases without signing in.

## N-sight RMM mode
`Unravel.exe --rmm [--out <folder>] [--result <file>] [--keep N] [--redact] [--csv] [--organisation <name>]` scans with
no window and returns the result as the exit code (0 pass, 1001 warnings, 1002 critical, 1010 error; see README.md).
It works as Unveil's `--rmm` does: `Program.Main` handles it before any WPF code starts, the logic is in
`src/Unravel.Core/Rmm/RmmMode.cs`, and `rmm/Unravel-NSight.ps1` is what N-sight runs (download, SHA-256 check, run,
print one line of at most 255 characters).

As Local System, `CollectContext.LiveForRmm` finds the person signed in at the screen (`ConsoleUser`: the console
session's user, else the only loaded user hive) and `LiveSystemSource.ForUser` reads HKCU from `HKEY_USERS\<SID>` and
their Store apps; their Startup folder comes from their profile. Nobody signed in: those are left out with a note.
dsregcmd's user state (PRT, work account) describes whoever runs it, so as Local System it is shown as unknown.

Test it as a standard user:
```
powershell -ExecutionPolicy Bypass -File rmm\Unravel-NSight.ps1 -Folder <folder with a built Unravel.exe> -NoDownload
```
To test it as N-sight runs it (Local System, no desktop), register a temporary scheduled task as `SYSTEM` that runs the
script with `-NoDownload` and writes its output to a file, start it, then unregister it. That needs one elevated
PowerShell (UAC). Keep the script plain ASCII (N-sight stores and runs it as uploaded).

## Icon, logo and social preview
The icon is a grey faceted CPU chip with a blue spiral disc in the middle, in the same flat faceted style, greys and
accent blue as the Unbound and Unveil icons. The disc is solid and large so the icon reads at 16 px; 32 px and
smaller use a bolder spiral with fewer turns and two larger pins per side. `assets/make_icon.py` draws everything
from code:
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
  - `Reports/`: the report document, redaction and the HTML, JSON and CSV writers.
  - `Rmm/`: the no-window N-sight mode: options, exit codes, the one-line result, report retention.
- `rmm/Unravel-NSight.ps1`: the script N-sight runs (downloads, runs and reports back).
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
| `--export <file.html\|.pdf\|.json\|.csv>` | Scans with no window and writes that export (the type comes from the extension). Add `--redact`, `--organisation <name>`, `--user <name>`, `--notes <text>`, or `--fixture <file.json>` to report on a captured machine. |

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
