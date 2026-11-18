// Модель сохраняемых настроек. Автосвойства get/set превращаются в поля JSON.
// Значения после = используются при первом запуске и при чтении старого JSON,
// в котором новых полей ещё нет. Ключи JSON не переводятся при смене языка.
// 
// Load пробует основной файл, затем резервную копию. Normalize проверяет диапазоны.
// Save сначала пишет отдельный временный файл и только затем атомарно заменяет
// основной: при сбое питания не должно остаться наполовину записанного JSON.
// Автозапуск не является полем JSON: его реальное состояние хранит Windows.

using Microsoft.Win32;
using System.Text.Json;
namespace LegionThermals;

public sealed class Settings
{
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
    public bool Banner { get; set; } = true;
    public bool Numbers { get; set; } = true;
    public double Opacity { get; set; } = .8;
    public int Interval { get; set; } = 3;
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? PreferencesX { get; set; }
    public int? PreferencesY { get; set; }
    public bool FanManualEnabled { get; set; }
    public int FanMinimumPercent { get; set; } = 50;
    public bool FanSchedulerEnabled { get; set; }
    public FanSchedulePoint[] FanSchedulePoints { get; set; } = FanSchedule.Defaults();
    private bool loadedFromBackup;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionThermals");
    // Повреждённый основной JSON не уничтожает резервную копию; сначала пробуем её прочитать.
    public static Settings Load(string? folder = null)
    {
        var path = Path.Combine(folder ?? Folder, "settings.json");
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                var json = File.ReadAllText(candidate);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s == null) continue;
                // Старые версии имели только русский интерфейс: сохраняем привычный язык.
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty(nameof(Language), out _)) s.Language = "ru";
                s.loadedFromBackup = candidate != path;
                s.Normalize();
                return s;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { }
        }
        return new();
    }
    // Ограничиваем значения из файла: пользователь мог изменить JSON вручную.
    private void Normalize()
    {
        if (Language is not ("ru" or "en")) Language = "ru";
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .2, 1) : .8;
        if (!new[] { 1, 2, 3, 5, 10, 30 }.Contains(Interval)) Interval = 3;
        FanMinimumPercent = FanPolicy.ClampPercent(FanMinimumPercent);
        if (FanSchedule.Validate(FanSchedulePoints) != null)
        {
            if (FanSchedulerEnabled) FanManualEnabled = false;
            FanSchedulePoints = FanSchedule.Defaults();
            FanSchedulerEnabled = false;
        }
        if (FanSchedulerEnabled) FanManualEnabled = false;
    }
    // Уникальное временное имя не позволяет двум сохранениям случайно использовать один файл.
    public void Save(string? folder = null)
    {
        Normalize();
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path) && !loadedFromBackup) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path, true);
            loadedFromBackup = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Startup => StartupTask.Enabled;
    public static void SetStartup(bool enabled) => StartupTask.Set(enabled);
}

