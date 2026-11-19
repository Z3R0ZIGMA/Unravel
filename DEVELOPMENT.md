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
- `src/Unravel.Core`: models, collectors, health checks and the Summary model. No UI references, so a CLI/RMM mode can reuse it.
  - `Infrastructure/`: `Wmi` (timeouts, disposes every WMI object, refuses `Win32_Product`), `ISystemSource` (live or
    fixture), `ScanRunner` (collectors in parallel, 20 s timeout each), `Elevation`, `OemValues` (OEM placeholders
    such as "To be filled by O.E.M." become "Not set").
  - `Collectors/`: one per section. A collector never throws to the app: failures become "Couldn't read X (reason)".
- `src/Unravel.App`: WPF app. `Themes/Dark.xaml` is Unveil's theme plus a few Unravel styles.
- `tests/Unravel.Core.Tests`: xUnit. `Fixtures/` holds captured system data replayed through `FixtureSystemSource`.

## Developer switches
| Switch | What it does |
|---|---|
| `--capture-fixture <file.json>` | Scans with no window and saves everything read (WMI + registry) as a fixture. |
| `--fixture <file.json>` | Shows a captured machine instead of this PC. |
| `--screenshot <file.png>` | Renders the Summary page to an image after the scan, then exits. |

Captured fixtures contain serial numbers, UUIDs and names: scrub them before committing to `tests/.../Fixtures/`
(the `scratch/` folder is git-ignored for raw captures).
