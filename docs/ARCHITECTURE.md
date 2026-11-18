# Architecture and code guide

The application intentionally avoids a sensor framework, background service or kernel driver. Native Windows COM APIs provide Lenovo WMI and Task Scheduler access. Avalonia provides the desktop UI; System.Drawing renders the small Windows notification icon.

## Start here

| File | Responsibility |
| --- | --- |
| `Program.cs` | Entry point, language initialization, diagnostic commands and single-instance mutex |
| `App.cs` | Banner, tray, settings editor, language switch and worker lifetime |
| `Settings.cs` | JSON defaults, validation, atomic save and backup recovery |
| `L.cs` | Thread-visible language choice and adjacent Russian/English strings |
| `SensorReader.cs` | Read-only temperatures, WMI3 with legacy fallback, diagnostic output |
| `WmiSession.cs` | COM queries/methods, input templates, instance-path cache and resource cleanup |
| `FanController.cs` | Validated model, actual RPM, transactional curve/mode/full-speed ownership and restore |
| `FanPolicy.cs` | Pure percentage-to-level conversion and 64-byte payload encoding |
| `FanSchedule.cs` | Pure threshold validation and cooling hysteresis |
| `StartupTask.cs` | Per-user highest-privilege interactive sign-in task, no saved password |
| `Icons.cs` | One CPU/GPU notification icon and native HICON cleanup |

All major source blocks contain Russian explanations for readers new to C#. Technical identifiers (WMI method names, JSON keys, task ownership markers) are deliberately never translated.

## Threads and data ownership

Avalonia controls and mutable Settings belong to the UI thread. A dedicated STA thread owns all WMI objects; COM objects are not passed to the UI. The loop reads sensors, selects a manual/scheduled request, applies control, posts a plain snapshot through `Dispatcher.UIThread.Post`, then waits on cancellation, a configuration-change event or the polling interval.

Simple values use Volatile access. Schedule edits create a sorted immutable snapshot, preventing the UI from modifying an array while the worker is reading it. Invalid draft rows do not replace the last valid snapshot. A 600 ms debounce merges rapid UI changes before saving.

## Fan ownership transaction

Before the first write, the controller validates hardware and saves the original SmartFanMode, ten-point curve and optional full-speed override in a recovery journal. It then selects Custom, writes a valid curve, checks the readback and disables the separate full-speed override that would otherwise defeat the curve. Measured RPM are read separately.

Auto, missing temperatures or a normal exit restore original state. External mode changes end application control. Restoration failure keeps the journal and prevents a silent normal exit. A forced process kill cannot execute finally; a later elevated launch recovers the journal first. Old journals without the optional full-speed field remain compatible.

The slider sets a minimum curve floor. The hardware still controls higher-temperature cooling. The application additionally requests the maximum curve at CPU90°C/GPU80°C. The scheduler uses the larger sensor temperature and a 3°C cooling deadband; it resets state when a table changes or sensors disappear.

## Language switching

`L.T(russian, english)` keeps translations next to each other. The selected `ru`/`en` is stored in Settings and read with Volatile by both threads. Switching recreates Settings and the native tray menu, then wakes the worker to refresh status messages; it does not stop the hardware controller or change its baseline. New profiles follow the OS language (Russian or English fallback); old profiles keep Russian until explicitly changed.

## Persistence and Windows startup

Settings are written to a unique temporary file, then atomically moved/replaced with a backup. Invalid ranges and unsupported language codes are normalized on load/save. A corrupt enabled schedule is disabled rather than silently becoming a different active schedule.

Task Scheduler owns startup state, not a JSON boolean. Each SID has a distinct app-owned task: interactive token, highest available run level, ten-second sign-in delay, unlimited duration and battery support. A normal executable launch uses requireAdministrator. Updating the app path updates only an already-enabled app-owned task. Uninstall removes only the current user's task pointing to that exact copy.

## Tests and packaging

The console test project uses no test framework package and returns nonzero on a failure. CI runs pure tests on Windows; it never invokes the hardware smoke test or registers a startup task. Local physical tests are separate and require an administrator console.

`Build-Installer.ps1` publishes self-contained .NET, gathers licenses from exact NuGet dependency metadata, generates a Unicode RTF agreement and an exact uninstall file list, then invokes NSIS. Never commit generated payloads, firmware dumps, private diagnostics or settings. Release artifacts belong in GitHub Releases.
