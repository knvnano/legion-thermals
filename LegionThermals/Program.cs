// Точка входа: Windows сначала проверяет UAC-манифест, потом вызывает Main.
// [STAThread] выбирает COM-режим, необходимый Windows и интерфейсу Avalonia.
// Служебные команды намеренно обрабатываются до mutex: диагностика и удаление
// задачи должны работать даже при уже запущенном мониторе.
// Mutex — именованный системный замок: не даёт двум копиям управлять вентиляторами.
// AbandonedMutexException означает, что предыдущая копия упала, а замок теперь наш.

using Avalonia;
namespace LegionThermals;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var preferences = Settings.Load();
        // Необязательный CLI-параметр полезен для воспроизводимой проверки перевода.
        // Обычный пользователь меняет язык кнопкой; протокол и JSON-ключи те же.
        var languageIndex = Array.IndexOf(args, "--language");
        if (languageIndex >= 0)
        {
            if (languageIndex + 1 >= args.Length || args[languageIndex + 1] is not ("ru" or "en"))
            { Environment.ExitCode = 2; return; }
            preferences.Language = args[languageIndex + 1];
            preferences.Save();
        }
        L.Language = preferences.Language;
        if (args.Contains("--enable-startup")) { Settings.SetStartup(true); return; }
        if (args.Contains("--disable-startup")) { Settings.SetStartup(false); return; }
        if (args.Contains("--remove-startup-for-this-copy")) { StartupTask.RemoveForThisCopy(); return; }
        // Diagnosis is independent of the running tray instance.
        if (args.Contains("--diagnose"))
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics.txt"), SensorReader.Diagnose());
            return;
        }
        using var mutex = new Mutex(false, @"Local\LegionThermals");
        bool acquired;
        try { acquired = mutex.WaitOne(args.Contains("--restart") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect()
                .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
                .StartWithClassicDesktopLifetime(args);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
