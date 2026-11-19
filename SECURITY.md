# Security policy

## Supported versions
Only the latest release gets security fixes. The N-sight script downloads the latest release unless it is pinned with
`-Version`, so update a pinned version when a fix is released.

## Reporting a vulnerability
Please report it privately: on GitHub, **Security > Report a vulnerability** on this repository. Don't open a public
issue. Say what is affected (the app, the N-sight script, the reports), how to reproduce it, and the version.

You should get a reply within a week. Fixes are released as a new version, with the issue described in
[CHANGELOG.md](CHANGELOG.md) once the release is out.

## How Unravel is meant to run
- The app runs as a standard user and only reads; it asks for administrator rights (UAC) only when the technician
  clicks **Restart as administrator**. It sends nothing anywhere (see the README's Privacy section).
- From N-able N-sight it runs as Local System. `rmm/Unravel-NSight.ps1` keeps `C:\ProgramData\Unravel` usable by
  SYSTEM and Administrators only, checks `Unravel.exe` against the release's `SHA256SUMS.txt` before every run, and never
  runs an exe that does not match.
- Releases are built from this repository by `build/publish.ps1`; each release lists the SHA-256 of every file. The
  exes are not code-signed yet.
