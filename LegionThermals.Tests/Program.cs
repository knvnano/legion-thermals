using System.Runtime.InteropServices;
using LegionThermals;

var checks = 0;
void Check(bool success, string description)
{
    if (!success) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
foreach (var invalid in new[] { int.MinValue, -1, 0, 126, int.MaxValue })
    Check(SensorReader.ValidTemperature(invalid) == null, $"Unknown/invalid {invalid} is not displayed as temperature");
foreach (var valid in new[] { 1, 51, 71, 100, 125 })
    Check(SensorReader.ValidTemperature(valid) == valid, $"Temperature {valid} retained");
foreach (var code in new[] { 0x80041003u, 0x80070005u })
{
    var error = new COMException("Access denied", unchecked((int)code));
    Check(SensorReader.IsAccessDenied(error), $"Recognizes access-denied 0x{code:X8}");
    Check(SensorReader.ExplainError(error, "query", false).Contains("Перезапустить"), "Standard token: offers elevation");
    Check(SensorReader.ExplainError(error, "query", true).Contains("даже с правами"), "Elevated token: does not falsely ask for elevation again");
}
var providerFailure = new COMException("Provider failure", unchecked((int)0x80041001));
Check(!SensorReader.IsAccessDenied(providerFailure), "Provider failure is not misreported as access denied");
Check(SensorReader.ExplainError(providerFailure, "GetFeatureValue", true).Contains("GetFeatureValue"), "Failure retains method name");
Console.WriteLine($"{checks} checks passed.");
var curve = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 8, 8 };
var cpuLadder = new[] { 1700, 1900, 2100, 2300, 2500, 2900, 3400, 3700, 4400, 5400 };
var gpuLadder = new[] { 1700, 1900, 2100, 2200, 2700, 2900, 3500, 3700, 4600, 5400 };
var previousFloor = 0;
for (var percent = 30; percent <= 100; percent++)
{
    var levels = FanPolicy.BuildLevels(curve, cpuLadder, gpuLadder, percent, false);
    Check(levels.Zip(curve).All(p => p.First >= p.Second), $"{percent}% never lowers the baseline curve");
    Check(levels[^1] == 10 && levels[^2] >= 9, $"{percent}% retains high-temperature cooling");
    Check(levels[0] >= previousFloor, $"{percent}% floor is monotonic");
    previousFloor = levels[0];
}
var maximum = FanPolicy.BuildLevels(curve, cpuLadder, gpuLadder, 100, false);
Check(maximum.All(x => x == 10), "100% selects maximum for every curve point");
Check(FanPolicy.BuildLevels(curve, cpuLadder, gpuLadder, 30, true).All(x => x == 10), "Hot override selects maximum even at low slider setting");
var packet = FanPolicy.Encode(maximum);
Check(packet.Length == 64 && packet[0] == 1, "Firmware packet has the expected length and header");
Check(Enumerable.Range(0, 10).All(i => packet[6 + 2 * i] == 10), "Firmware levels occupy only the documented offsets");
Check(Enumerable.Range(1, 63).Where(i => i < 6 || i > 24 || i % 2 == 1).All(i => packet[i] == 0), "Reserved payload bytes remain zero");
var folder = Path.Combine(Path.GetTempPath(), "LegionThermals-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var settings = new Settings { Opacity = .62, X = 600, Y = 80, Numbers = false, Interval = 10, FanManualEnabled = true, FanMinimumPercent = 65, PreferencesX = 250, PreferencesY = 100 };
    settings.Save(folder);
    var restored = Settings.Load(folder);
    Check(restored.FanManualEnabled && restored.FanMinimumPercent == 65 && restored.Opacity == .62 && restored.X == 600 && restored.PreferencesY == 100, "Appearance, fan and window settings survive restart");
    settings.FanMinimumPercent = 80; settings.Save(folder);
    File.WriteAllText(Path.Combine(folder, "settings.json"), "{broken");
    restored = Settings.Load(folder);
    Check(restored.FanMinimumPercent == 65, "Corrupt settings recover from the last good backup");
    restored.Save(folder);
    Check(Settings.Load(folder).FanMinimumPercent == 65, "Recovered settings can be saved without losing their backup");
    File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"Opacity\":4,\"Interval\":4,\"FanMinimumPercent\":-5}");
    restored = Settings.Load(folder);
    Check(restored.Opacity == 1 && restored.Interval == 3 && restored.FanMinimumPercent == 30, "Out-of-range persisted values are normalized");
}
finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
var plan = new[] { new FanSchedulePoint(40, 30), new FanSchedulePoint(60, 60), new FanSchedulePoint(80, 100) };
Check(FanSchedule.Validate(plan) == null, "A valid temperature schedule is accepted");
var scheduler = new FanSchedule();
Check(scheduler.Select(plan, 39) == 30, "Below the first threshold requests 30%");
Check(scheduler.Select(plan, 59) == 30, "Below the next threshold retains the lower step");
Check(scheduler.Select(plan, 60) == 60, "Reaching a threshold accelerates immediately");
Check(scheduler.Select(plan, 59) == 60 && scheduler.Select(plan, 57) == 60, "Three-degree hysteresis prevents fan oscillation");
Check(scheduler.Select(plan, 56) == 30, "Cooling past hysteresis reduces the requested speed");
Check(scheduler.Select(plan, 90) == 100, "A temperature jump selects the highest reached step");
Check(scheduler.Select(plan, 45) == 30, "Cooling across several thresholds skips directly down");
scheduler.Reset(); Check(scheduler.Select(plan, 58) == 30, "Reset after sensor loss clears the previous hot step");
Check(FanSchedule.Validate(new[] { new FanSchedulePoint(50, 40), new FanSchedulePoint(50, 60) }) != null, "Duplicate temperature thresholds are rejected");
Check(FanSchedule.Validate(new[] { new FanSchedulePoint(50, 80), new FanSchedulePoint(60, 40) }) != null, "A hotter threshold cannot request lower cooling");
Check(FanSchedule.Validate(new[] { new FanSchedulePoint(50, 35) }) != null, "Schedule speeds must be multiples of 10");
Check(FanSchedule.Validate(new[] { new FanSchedulePoint(10, 30) }) != null, "Out-of-range schedule temperatures are rejected");
Check(FanSchedule.Validate(Array.Empty<FanSchedulePoint>()) != null, "An empty schedule is rejected");
var scheduleFolder = Path.Combine(Path.GetTempPath(), "LegionSchedule-test-" + Guid.NewGuid().ToString("N"));
try
{
    var scheduled = new Settings { FanSchedulerEnabled = true, FanManualEnabled = true, FanSchedulePoints = plan };
    scheduled.Save(scheduleFolder);
    var loaded = Settings.Load(scheduleFolder);
    Check(loaded.FanSchedulerEnabled && !loaded.FanManualEnabled && loaded.FanSchedulePoints.SequenceEqual(plan), "The schedule survives restart and excludes manual mode");
    File.WriteAllText(Path.Combine(scheduleFolder, "settings.json"), "{\"FanSchedulerEnabled\":true,\"FanSchedulePoints\":[{\"Temperature\":50,\"Percent\":5}]}");
    loaded = Settings.Load(scheduleFolder);
    Check(!loaded.FanSchedulerEnabled && FanSchedule.Validate(loaded.FanSchedulePoints) == null, "Corrupt saved schedule cannot silently activate control");
}
finally { if (Directory.Exists(scheduleFolder)) Directory.Delete(scheduleFolder, true); }
L.Language = "en";
Check(L.T("Русский", "English") == "English", "English UI text is selected");
Check(FanSchedule.Validate(new[] { new FanSchedulePoint(50, 35) })!.Contains("steps of 10"), "Schedule validation is translated to English");
Check(SensorReader.ExplainError(new UnauthorizedAccessException(), "WMI", false).Contains("administrator privileges"), "Sensor access guidance is translated to English");
L.Language = "unsupported";
Check(L.T("Русский", "English") == "Русский", "Unknown runtime language falls back to Russian");
var languageFolder = Path.Combine(Path.GetTempPath(), "Legion-language-" + Guid.NewGuid().ToString("N"));
try
{
    new Settings { Language = "en" }.Save(languageFolder);
    Check(Settings.Load(languageFolder).Language == "en", "Language selection survives restart");
    new Settings { Language = "invalid" }.Save(languageFolder);
    Check(Settings.Load(languageFolder).Language == "ru", "Invalid saved language is normalized");
}
finally { if (Directory.Exists(languageFolder)) Directory.Delete(languageFolder, true); }
Console.WriteLine($"TOTAL: {checks} checks passed.");


