# Contributing to Unravel

Thanks for helping. Unravel is a small tool kept by one person, so please open an issue before starting on a big
change.

- **Bugs:** use the bug report form under Issues. Don't attach reports: they contain serial numbers, names and
  addresses. If one is needed, export it with **Redact sensitive fields** on and check it first.
- **Security problems:** report them privately (see [SECURITY.md](SECURITY.md)), never as an issue.
- **Code:** [DEVELOPMENT.md](DEVELOPMENT.md) explains building, testing, the layout and the ground rules. The ones
  that matter most:
  - no telemetry: Unravel sends nothing anywhere unless the user clicks something that says so, and the scan only
    reads
  - never use `Win32_Product`; every collector handles its own failures and shows "Couldn't read X (reason)"
  - no personal or customer data in the repository: test fixtures are scrubbed or synthetic
  - anything that runs as SYSTEM follows the security rules in DEVELOPMENT.md
  - `dotnet build` and `dotnet test` must pass (warnings are errors)

By contributing you agree that your work is released under the [MIT licence](LICENSE).
