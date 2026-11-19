<#
.SYNOPSIS
  Runs the Unravel PC check from N-able N-sight RMM (Script Check or Automated Task).

.DESCRIPTION
  N-sight runs this script as Local System with no screen. It:
    0. makes sure C:\ProgramData\Unravel can only be used by SYSTEM and Administrators (a folder anyone could change
       is moved aside, unopened, and a new one made), so no standard user can swap files run as SYSTEM or read the
       reports;
    1. downloads Unravel.exe from the Unravel GitHub release into C:\ProgramData\Unravel (only when it has changed),
       and checks it against the release's SHA256SUMS.txt before every run; an exe that does not match never runs;
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
$sumsFile = Join-Path $Folder 'SHA256SUMS.txt'

# ------------------------------------------------------------------ 0. a folder only SYSTEM and Administrators can use
# N-sight runs this as Local System. C:\ProgramData lets every user create folders and files, so a standard user could
# create the folder first, or drop a file (a DLL, a fake Unravel.exe) next to the exe and have it run as SYSTEM. So the
# folder must belong to SYSTEM/Administrators with no other access (that also keeps the reports private). A folder that
# is not like that is moved aside unopened (it may hold links that point elsewhere) and a new one is made.
$Trusted = @('S-1-5-18', 'S-1-5-32-544')   # Local System, Administrators

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-SecureFolder([string]$Path) {
    try {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }   # a junction or link
        $acl = Get-Acl -LiteralPath $Path
        if (-not $acl.AreAccessRulesProtected) { return $false }                        # inherits ProgramData's access
        if ($Trusted -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { return $false }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($Trusted -notcontains $rule.IdentityReference.Value) { return $false }
        }
        return $true
    } catch {
        return $false
    }
}

function New-SecureFolder([string]$Path) {
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in $Trusted) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    # Created with its permissions in one step, so nobody can add a file in between.
    if ($PSVersionTable.PSVersion.Major -le 5) {
        [void][IO.Directory]::CreateDirectory($Path, $security)                              # Windows PowerShell
    } else {
        [void][IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo $Path), $security)   # PowerShell 7
    }
}

$movedAside = $null

if (Test-Elevated) {
    try {
        if ((Test-Path -LiteralPath $Folder) -and -not (Test-SecureFolder $Folder)) {
            $aside = "$Folder.untrusted-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
            [IO.Directory]::Move($Folder, $aside)
            $movedAside = $aside
            # Best effort: rd removes links themselves and never follows them.
            & $env:ComSpec /c rd /s /q "`"$aside`"" 2>$null | Out-Null
        }
        if (-not (Test-Path -LiteralPath $Folder)) { New-SecureFolder $Folder }
        if (-not (Test-SecureFolder $Folder)) { throw "its permissions could not be set to SYSTEM and Administrators only" }
    } catch {
        Finish 1010 "Unravel: cannot secure $Folder ($($_.Exception.Message))"
    }
} else {
    # Run by a standard user (a test): nothing runs with more rights than they have, so an ordinary folder is fine.
    try {
        New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    } catch {
        Finish 1010 "Unravel: cannot create $Folder ($($_.Exception.Message))"
    }
}

function Get-ExpectedHash([string]$Asset) {
    if (-not (Test-Path -LiteralPath $sumsFile -PathType Leaf)) { return $null }
    $line = Get-Content -LiteralPath $sumsFile | Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($Asset))\s*$" } | Select-Object -First 1
    if (-not $line) { return $null }
    return ($line -split '\s+')[0].ToLowerInvariant()
}

$arch = $env:PROCESSOR_ARCHITEW6432
if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
$asset = if ($arch -eq 'ARM64') { 'Unravel-arm64.exe' } else { 'Unravel.exe' }

# ------------------------------------------------------------------ 1. get Unravel.exe
$downloadError = $null
if (-not $NoDownload) {
    $base = if ($Version -eq 'latest') { "https://github.com/$Repo/releases/latest/download" }
            else { "https://github.com/$Repo/releases/download/v$($Version.TrimStart('v'))" }

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $sumsDownload = "$sumsFile.download"
        Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS.txt" -OutFile $sumsDownload -TimeoutSec 60
        Move-Item -LiteralPath $sumsDownload -Destination $sumsFile -Force
        $expected = Get-ExpectedHash $asset
        if (-not $expected) { throw "$asset is not listed in SHA256SUMS.txt" }

        $current = if (Test-Path -LiteralPath $exe) { (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
        if ($current -ne $expected) {
            $download = "$exe.download"
            Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $download -TimeoutSec 600
            $got = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($got -ne $expected) {
                Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
                throw "the downloaded file does not match SHA256SUMS.txt"
            }
            Move-Item -LiteralPath $download -Destination $exe -Force
        }
    } catch {
        # A copy from an earlier run may still be used, but only if it matches the checksums saved with it (below).
        $downloadError = $_.Exception.Message
    }
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    if ($downloadError) {
        Finish 1011 "Unravel could not be downloaded ($downloadError). Check that github.com is reachable, or copy Unravel.exe to $Folder and use -NoDownload."
    }
    if ($movedAside) {
        Finish 1011 "$Folder could be changed by any user, so it was replaced by a locked-down one. Copy Unravel.exe into it again, or run without -NoDownload."
    }
    Finish 1011 "Unravel.exe is not in $Folder. Copy it there, or run without -NoDownload."
}

# Check the exe on every run, not only after a download. With -NoDownload and no checksum file (an exe copied in by
# hand), the folder's permissions are what protect it: only SYSTEM and Administrators can put a file there.
$expected = Get-ExpectedHash $asset
if ($expected) {
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        Finish 1011 "Unravel.exe in $Folder does not match SHA256SUMS.txt, so it was not run.$(if ($downloadError) { " The download failed: $downloadError" })"
    }
} elseif (-not $NoDownload) {
    Finish 1011 "Unravel could not be checked: no SHA256SUMS.txt$(if ($downloadError) { " ($downloadError)" }). Check that github.com is reachable."
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
