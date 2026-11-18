# Contributing

Bug reports, documentation fixes and pull requests are welcome. Read the [architecture guide](docs/ARCHITECTURE.md) and [hardware scope](docs/HARDWARE.md) first.

## Development

Use Windows x64 and the.NET10 SDK. Build the app and run the console checks:

```powershell
dotnet restore LegionThermals/LegionThermals.csproj
dotnet build LegionThermals/LegionThermals.csproj -c Release
dotnet run --project LegionThermals.Tests -c Release
```

Keep native hardware writes in FanController and pure policy in FanPolicy/FanSchedule. UI code must not call WMI directly. Every new user-facing message should supply both languages through L.T. Keep JSON keys and protocol identifiers stable. Add a regression check for changed scheduling or recovery behavior.

## Hardware validation

Ordinary tests do not touch cooling. The optional `LegionThermals.FanSmoke` console project with `--apply` changes fan requests40→80→40 and restores original state in finally. It requires an administrator console, the validated machine and no other running Legion Thermals controller. Do not enable writes for an unknown model merely because a WMI method exists or table readback succeeds: verify firmware units, physical RPM response and restoration first.

## Reports and privacy

Include app version, machine type, BIOS version, expected/observed behavior and relevant redacted errors. Review diagnostic output before sharing. Do not commit tokens, settings, user paths, SID values, DSDT dumps, release payloads or local development reports. Do not disclose vulnerabilities through public bug reports; see SECURITY.md.

## Releases

Bump the project/manifest and installer default version, update CHANGELOG.md, build self-contained artifacts using the documented script and include original third-party notices. Publish binaries and SHA256 checksums as release assets, not Git blobs. State which physical checks were actually performed. Packaging alone is not evidence of fan-speed control.

Contributions are provided under the repository's MIT license.
