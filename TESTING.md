# Release testing

What to check before a release (plan.md Phase 5). The automated tests (`dotnet test`, run by `build\publish.ps1`)
replay captured and synthetic machines from `tests/Unravel.Core.Tests/Fixtures`; this list covers what only a real PC
can show.

## Machines
Run the release `Unravel.exe` on each and tick it off. "Fixture" means the parsing is covered by an automated test
with data shaped like that machine, but the exe has not run on one yet.

| Machine | Windows | Standard user | Administrator | Notes |
|---|---|---|---|---|
| ASUS ExpertBook B1403CVA laptop | 11 26H2 (build 26300) | Done (0.5.0) | | Scan, all pages, disk/network/CPU tests, HTML/PDF/JSON/CSV exports |
| Dell laptop | 10 22H2 or 11 | | | Fixture: hybrid Entra joined, Intune, problem devices, worn battery |
| HP laptop | 11 | | | Warranty button copies the serial and opens HP's page |
| Lenovo laptop | 11 | | | Warranty button opens Lenovo's page with the serial |
| Desktop (no battery) | 10 22H2 or 11 | | | Fixture: whitebox with OEM placeholder values, no battery |
| Hyper-V or VMware VM | 11 | | | Fixture: Hyper-V with no TPM and limited data |
| Windows 10 22H2 PC | 10 22H2 | | | Windows 11 readiness, Windows 10 end of support shown |
| Windows 11 23H2 / 24H2 / 25H2 | each | | | Support dates per version |
| Windows on ARM (Snapdragon) | 11 | | | `Unravel-arm64.exe`; Windows 11 readiness places Snapdragon |

## On each machine
1. **Start:** the exe opens without installing anything; SmartScreen's "More info > Run anyway" is the only prompt.
2. **Scan:** every section fills in within about 20 s; anything unreadable says "Couldn't read X (reason)" and the rest
   still shows. No crash or error box.
3. **Summary:** make, model and serial are right (placeholders such as "To be filled by O.E.M." show "Not set"), and
   CPU, RAM (with DDR generation), disks with free space and the Windows version match Settings > System > About.
4. **Pages:** Device, Hardware, OS, Software, Security and Diagnostics show sensible values; hover a few status icons.
5. **Standard user:** the "needs administrator" banner shows; BitLocker, SMART and crash dumps say they need it.
6. **Restart as administrator:** one UAC prompt; BitLocker, TPM details, SMART and crash dumps fill in; the system
   file test can run.
7. **Active tests:** disk speed, network and CPU stress finish and can be cancelled; the memory diagnostic opens.
8. **Reports:** save HTML, PDF, JSON and CSV; open each. Repeat with **Redact sensitive fields** and check the serial,
   computer name and user name do not appear.
9. **Closing** the window leaves no process behind, and nothing is left in `%TEMP%` except `%TEMP%\Unravel\WebView2`
   after a PDF.

## From N-sight (or as Local System)
1. **Standard user, no N-sight:** `powershell -ExecutionPolicy Bypass -File rmm\Unravel-NSight.ps1 -Folder <folder with
   the built Unravel.exe> -NoDownload` prints one line, exits 0, 1001 or 1002, and leaves an HTML and JSON report in
   `<folder>\Reports`.
2. **As Local System:** run the script from a temporary scheduled task as `SYSTEM` (see DEVELOPMENT.md). BitLocker,
   TPM and SMART are filled in; startup programs and the Microsoft 365 account are the signed-in user's; no window
   appears.
3. **From N-sight:** an Automated Task with `-ReportOnly` shows green and the line; a Script Check fails (red) when
   there is a warning. The report can be downloaded with File transfer.
4. **Download:** without `-NoDownload` the script fetches the release's `Unravel.exe` and checks it against
   `SHA256SUMS.txt`; a second run does not download again.
