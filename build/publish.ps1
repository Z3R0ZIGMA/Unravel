<#
.SYNOPSIS
  Builds the release: a single-file, self-contained Unravel.exe for win-x64 and win-arm64 (as Unveil's build does).

.DESCRIPTION
  Output: dist\Unravel-<version>\win-x64\ and dist\Unravel-<version>\win-arm64\, each with
    Unravel.exe   the app (no .NET install needed on the PC)
    README.txt    how to use it
  and dist\Unravel-<version>\github\ with what goes on the GitHub release: Unravel.exe (x64), Unravel-arm64.exe and
  SHA256SUMS.txt.

  Publish settings (self-contained, single file) live in src\Unravel.App\Unravel.App.csproj, not here. The tests run
  first; a failing test stops the build.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\publish.ps1
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$dist = Join-Path $root "dist\Unravel-$version"

if (-not $SkipTests) {
    Write-Host "Running the tests..."
    & dotnet test (Join-Path $root "tests\Unravel.Core.Tests") --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "The tests failed: no release built." }
}

# Keep only the release being built: older versions are on GitHub if they are ever needed.
Get-ChildItem (Join-Path $root "dist") -Directory -Filter "Unravel-*" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "Removing old release $($_.Name)"; Remove-Item $_.FullName -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

foreach ($rid in "win-x64", "win-arm64") {
    $out = Join-Path $dist $rid
    Write-Host "Publishing $rid..."
    & dotnet publish (Join-Path $root "src\Unravel.App") -c $Configuration -r $rid -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($rid) failed" }

    # Debug symbols are embedded; drop any loose files that are not part of the release.
    Get-ChildItem $out -File | Where-Object { $_.Extension -ne ".exe" } | Remove-Item -Force

    @"
Unravel $version - Everything about this PC.

Double-click Unravel.exe. Nothing to install; it runs as a standard user and changes nothing on the PC.

- Summary: make, model, serial number, the key specs and a health check of every area.
- Device, Hardware, OS, Software and Security: the full inventory. Serials, MAC addresses and keys have a copy button.
- Diagnostics: health checks, Windows 11 readiness, Windows support and updates, stability (blue screens, crashes,
  disk errors, Reliability Monitor), and tests that run only when you click Run: disk speed, network, CPU stress,
  system files (sfc and DISM) and Windows Memory Diagnostic.
- Reports: save an HTML or PDF report with your organisation, user and notes, the JSON data, or the software list
  as CSV. "Redact sensitive fields" hides serials, names, addresses and IDs for reports going to a third party.
- Copy summary: a short text summary for a ticket.
- Restart as administrator: adds BitLocker, TPM details, disk health (SMART), crash dumps and the sfc/DISM test.

Privacy: Unravel sends nothing anywhere and has no telemetry. The network test (pings and DNS lookups) and the
warranty links only run when you click them. Reports contain serial numbers and names unless you redact them:
treat them as client data. Details: https://github.com/Z3R0ZIGMA/Unravel#privacy

If Windows shows "Windows protected your PC", click More info > Run anyway (the app is not code-signed yet).
"@ | Set-Content (Join-Path $out "README.txt") -Encoding UTF8
}

# The GitHub release offers the exes themselves, not zips (as Unveil and Unbound do). Unzipping with Windows copies the
# downloaded-from-the-internet mark onto the exe inside, so SmartScreen checks it again when it is opened.
$github = Join-Path $dist "github"
New-Item -ItemType Directory -Force $github | Out-Null
Copy-Item (Join-Path $dist "win-x64\Unravel.exe") (Join-Path $github "Unravel.exe")
Copy-Item (Join-Path $dist "win-arm64\Unravel.exe") (Join-Path $github "Unravel-arm64.exe")
$sums = Get-ChildItem $github -File -Filter *.exe | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $github "SHA256SUMS.txt") -Encoding ASCII

Write-Host ""
Write-Host "Release $version in $dist"
Get-ChildItem $dist -Recurse -File | ForEach-Object { "  {0,-28} {1,8:N1} MB" -f $_.FullName.Substring($dist.Length + 1), ($_.Length / 1MB) }
