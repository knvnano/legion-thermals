// Повышенный автозапуск через штатный Windows Task Scheduler COM API.
// Задача привязана к SID текущего пользователя: это постоянный идентификатор
// учётной записи, который не меняется при переименовании пользователя.
// InteractiveToken означает запуск в его рабочем столе без сохранения пароля;
// Highest использует доступный этому администратору повышенный токен.
// Числа COM-перечислений: logon=3, runLevel=1, logonTrigger=9, execAction=0,
// register CREATE_OR_UPDATE=6, MultipleInstances=2 (не запускать вторую копию).
// Маркер Description защищает чужую одноимённую задачу от перезаписи.
// Список owned хранит полученные COM-ссылки, чтобы освободить их в обратном порядке.

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace LegionThermals;

// Windows Task Scheduler COM; interactive logon, highest token, no stored password.
internal sealed class StartupTask : IDisposable
{
    private static string Name { get { using var identity = WindowsIdentity.GetCurrent(); return "LegionThermals-" + identity.User!.Value; } }
    private const string LegacyName = "LegionThermals";
    private const string Marker = "LegionThermals automatic startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly List<object> owned = new();
    private readonly dynamic folder;
    private dynamic Own(object value) { owned.Add(value); return value; }
    public StartupTask()
    {
        try
        {
            dynamic service = Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
            service.Connect();
            folder = Own(service.GetFolder("\\"));
        }
        catch { Dispose(); throw; }
    }
    // Отсутствующая задача — нормальная ситуация первого запуска, а не ошибка установки.
    private dynamic? Existing()
    {
        try { return Own(folder.GetTask(Name)); }
        catch (Exception e) when (unchecked((uint)e.HResult) is 0x80070002 or 0x8004130F) { return null; }
    }
    // По описанию отличаем созданную нами задачу от совпадения имени.
    private bool IsOurs(dynamic task)
    {
        dynamic definition = Own(task.Definition);
        dynamic registration = Own(definition.RegistrationInfo);
        return (string)registration.Description == Marker;
    }
    // Проверяем Windows при показе настройки, а не доверяем устаревшему флагу в JSON.
    public static bool Enabled
    {
        get
        {
            try
            {
                using var session = new StartupTask();
                dynamic? task = session.Existing();
                return task != null && session.IsOurs(task!) && (bool)task!.Enabled;
            }
            catch { return false; }
        }
    }
    public static bool LegacyEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(LegacyName) != null; }
    }
    // После переноса/установки обновляем путь только уже включённого нашего автозапуска.
    public static void RefreshPathIfEnabled()
    {
        using var session = new StartupTask();
        dynamic? task = session.Existing();
        if (task == null || !session.IsOurs(task!) || !(bool)task!.Enabled) return;
        dynamic definition = session.Own(task!.Definition);
        dynamic actions = session.Own(definition.Actions);
        dynamic action = session.Own(actions.Item(1));
        if (!string.Equals((string)action.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) Set(true);
    }
    // Удаление другой копии приложения не должно выключать автозапуск действующей копии.
    public static void RemoveForThisCopy()
    {
        using var session = new StartupTask();
        dynamic? task = session.Existing();
        if (task == null || !session.IsOurs(task!)) return;
        dynamic definition = session.Own(task!.Definition);
        dynamic actions = session.Own(definition.Actions);
        dynamic action = session.Own(actions.Item(1));
        if (string.Equals((string)action.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) Set(false);
    }
    // Регистрация выполняется повышенным процессом; старый Run удаляем только после успеха.
    public static void Set(bool enabled)
    {
        using var session = new StartupTask();
        dynamic? existing = session.Existing();
        if (existing != null && !session.IsOurs(existing))
            throw new InvalidOperationException(L.T("Задача LegionThermals уже существует и создана другой программой. Она не изменена.", "A task named LegionThermals already belongs to another application. It was not changed."));
        if (enabled)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!.Value;
            dynamic service = session.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
            service.Connect();
            dynamic task = session.Own(service.NewTask(0));
            dynamic info = session.Own(task.RegistrationInfo); info.Description = Marker;
            dynamic principal = session.Own(task.Principal);
            principal.UserId = sid; principal.LogonType = 3; principal.RunLevel = 1;
            dynamic triggers = session.Own(task.Triggers);
            dynamic logon = session.Own(triggers.Create(9)); logon.UserId = sid; logon.Delay = "PT10S";
            dynamic actions = session.Own(task.Actions);
            dynamic action = session.Own(actions.Create(0));
            action.Path = Environment.ProcessPath!; action.WorkingDirectory = AppContext.BaseDirectory;
            dynamic options = session.Own(task.Settings);
            options.Enabled = true; options.DisallowStartIfOnBatteries = false; options.StopIfGoingOnBatteries = false;
            options.ExecutionTimeLimit = "PT0S"; options.MultipleInstances = 2;
            session.Own(session.folder.RegisterTaskDefinition(Name, task, 6, sid, null, 3));
        }
        else if (existing != null) session.folder.DeleteTask(Name, 0);
        // Only remove the legacy Run entry after successful registration/deletion.
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(LegacyName, false);
    }
    // Освобождаем вложенные COM-объекты до их родителей.
    public void Dispose()
    {
        for (var i = owned.Count - 1; i >= 0; i--)
            if (Marshal.IsComObject(owned[i])) Marshal.ReleaseComObject(owned[i]);
        owned.Clear();
    }
}
