<#
.SYNOPSIS
  Runs the Unravel PC check from N-able N-sight RMM (Script Check or Automated Task).

.DESCRIPTION
  N-sight runs this script as Local System with no screen. It:
    1. downloads Unravel.exe from the Unravel GitHub release into C:\ProgramData\Unravel (only when it has changed),
       and checks it against the release's SHA256SUMS.txt;
    2. runs "Unravel.exe --rmm", which reads the whole PC (hardware, Windows, software, security, updates, stability)
       and runs every health check, with administrator rights. Per-user details (startup programs, programs installed
       for one user, Store apps, the Microsoft 365 account) come from the person signed in at the screen;
    3. saves the full HTML and JSON report in C:\ProgramData\Unravel\Reports (the newest 30 runs are kept);
    4. prints one line for the N-sight dashboard (N-sight shows the first 255 characters) and exits with:
         0     pass: no problems found (information only)
         1001  warnings: something is worth a look
         1002  critical: something needs action
         1010  Unravel failed (the line says why)
         1011  Unravel could not be downloaded and no copy is on the PC
         1012  Unravel was blocked, or did not finish in time

  Unravel itself sends nothing anywhere; only this script downloads, from the Unravel releases page.
  If the PC cannot reach github.com, copy Unravel.exe into C:\ProgramData\Unravel (for example with
  Remote Background > File transfer) and run this script with -NoDownload.

.PARAMETER Version
  The Unravel release to use, e.g. 0.6.0. Default "latest". Pin a version to update clients only when you choose.

.PARAMETER NoDownload
  Use the Unravel.exe already in the folder; never download.

.PARAMETER Keep
  How many runs' reports to keep. Default 30.

.PARAMETER ReportOnly
  Always pass (exit 0) when Unravel ran, whatever it found; the line still gives the result. For Automated Tasks,
  where you only want the report. Real failures (1010-1012) still fail. Leave it off for Script Checks, so a
  problem raises an alert.

.PARAMETER Redact
  Hide serial numbers, names, addresses and IDs in the reports (for reports that go to a third party).

.PARAMETER SoftwareCsv
  Also save the software list as a CSV file next to the report.

.PARAMETER Organisation
  The organisation name to put at the top of the report.

.PARAMETER Folder
  Where Unravel.exe and the Reports folder live. Default C:\ProgramData\Unravel.

.EXAMPLE
  N-sight "Command Line" box:  -Version 0.6.0 -SoftwareCsv -Organisation "Contoso Ltd"
  Automated Task that should show green when it ran:  -ReportOnly
#>
param(
    [string]$Version = 'latest',
    [switch]$NoDownload,
    [int]$Keep = 30,
    [switch]$ReportOnly,
    [switch]$Redact,
    [switch]$SoftwareCsv,
    [string]$Organisation = '',
    [string]$Folder = (Join-Path $env:ProgramData 'Unravel')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads very slow in Windows PowerShell
$Repo = 'Z3R0ZIGMA/Unravel'

function Finish([int]$Code, [string]$Text) {
    if ($Text.Length -gt 255) { $Text = $Text.Substring(0, 255) }
    Write-Host $Text
    exit $Code
}

$exe = Join-Path $Folder 'Unravel.exe'
$reports = Join-Path $Folder 'Reports'
$resultFile = Join-Path $Folder 'last-result.txt'

try {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
} catch {
    Finish 1010 "Unravel: cannot create $Folder ($($_.Exception.Message))"
}

# ------------------------------------------------------------------ 1. get Unravel.exe
if (-not $NoDownload) {
    $arch = $env:PROCESSOR_ARCHITEW6432
    if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
    $asset = if ($arch -eq 'ARM64') { 'Unravel-arm64.exe' } else { 'Unravel.exe' }
    $base = if ($Version -eq 'latest') { "https://github.com/$Repo/releases/latest/download" }
            else { "https://github.com/$Repo/releases/download/v$($Version.TrimStart('v'))" }

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $sumsFile = Join-Path $Folder 'SHA256SUMS.txt'
        Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS.txt" -OutFile $sumsFile -TimeoutSec 60
        $line = Get-Content $sumsFile | Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($asset))\s*$" } | Select-Object -First 1
        if (-not $line) { throw "$asset is not listed in SHA256SUMS.txt" }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()

        $current = if (Test-Path $exe) { (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
        if ($current -ne $expected) {
            $download = "$exe.download"
            Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $download -TimeoutSec 600
            $got = (Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($got -ne $expected) {
                Remove-Item $download -Force -ErrorAction SilentlyContinue
                throw "the downloaded file does not match SHA256SUMS.txt"
            }
            Move-Item $download $exe -Force
        }
    } catch {
        if (-not (Test-Path $exe)) {
            Finish 1011 "Unravel could not be downloaded ($($_.Exception.Message)). Check that github.com is reachable, or copy Unravel.exe to $Folder and use -NoDownload."
        }
        # A copy is already here: use it.
    }
}

if (-not (Test-Path $exe)) {
    Finish 1011 "Unravel.exe is not in $Folder. Copy it there, or run without -NoDownload."
}

# Files downloaded by a browser carry a "from the internet" mark that makes SmartScreen check them. This copy
# was not downloaded by a browser, but remove any mark anyway.
Unblock-File -Path $exe -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ 2. run it
Remove-Item $resultFile -Force -ErrorAction SilentlyContinue
$arguments = @('--rmm', '--out', "`"$reports`"", '--result', "`"$resultFile`"", '--keep', $Keep)
if ($Redact) { $arguments += '--redact' }
if ($SoftwareCsv) { $arguments += '--csv' }
if ($Organisation) { $arguments += @('--organisation', "`"$($Organisation.Replace('"', ''))`"") }

try {
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $null = $process.Handle   # needed for ExitCode to be available later in Windows PowerShell
} catch {
    Finish 1012 "Windows or the antivirus blocked Unravel.exe ($($_.Exception.Message)). Allow C:\ProgramData\Unravel in the antivirus policy."
}

# A scan takes 10-60 seconds; each part of it gives up after 20 seconds.
$limit = 240
if (-not $process.WaitForExit($limit * 1000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Finish 1012 "Unravel did not finish within $limit seconds and was stopped."
}

# ------------------------------------------------------------------ 3. report back to N-sight
$code = $process.ExitCode
$text = if (Test-Path $resultFile) { (Get-Content $resultFile -Raw).Trim() } else { "Unravel ended with code $code without a result." }

# -ReportOnly: a finding (1001-1002) is a result, not a failure of the task.
if ($ReportOnly -and $code -ge 1001 -and $code -le 1002) { $code = 0 }
Finish $code $text
