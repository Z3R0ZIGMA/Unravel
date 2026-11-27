## What this changes


## Checks
- [ ] `dotnet build Unravel.sln` and `dotnet test Unravel.sln` pass
- [ ] No serial numbers, computer or user names, SIDs, IP or MAC addresses, Wi-Fi or tenant names added (fixtures are scrubbed or synthetic; raw captures stay in `scratch/`)
- [ ] Still no telemetry, nothing sent anywhere unless the user clicks, and the scan still only reads
- [ ] No `Win32_Product`, and a failing collector still shows "Couldn't read X" instead of crashing
- [ ] README.md (Privacy section included), DEVELOPMENT.md and CHANGELOG.md updated if behaviour changed
- [ ] `rmm/Unravel-NSight.ps1` is still plain ASCII, and the release asset names are unchanged
