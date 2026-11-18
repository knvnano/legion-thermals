// Чтение температур: никаких записей в охлаждение в этом классе нет.
// Основной путь — Lenovo WMI3 GetFeatureValue; резервный — старый Gamezone API.
// Идентификаторы ниже обозначают ТЕКУЩУЮ температуру, не температурный лимит.
// Недоступное/неправдоподобное показание представляется null, а не нулём градусов.
// trace нужен для локальной диагностики; секреты и сетевые запросы не используются.
// Если оба датчика сломались, COM-сеанс сбрасывается, чтобы следующий опрос
// мог переподключиться вместо бесконечного повторения ошибки на старом объекте.

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
namespace LegionThermals;

public sealed record Reading(int? Cpu, int? Gpu, string Status);

public sealed class SensorReader : IDisposable
{
    // Lenovo WMI3: current temperatures, not the configurable thermal limits.
    private const uint CpuTemperatureId = 0x05040000;
    private const uint GpuTemperatureId = 0x05050000;
    private dynamic? service;
    private readonly Dictionary<string, string> paths = new();
    private readonly List<string> trace = new();
    private string stage = L.T("подключение", "connection");
    // Проверяем фактический токен процесса, а не членство пользователя в группе без повышения.
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    // Создаём SWbemLocator и подключаемся к пространству имён Lenovo.
    private void Connect()
    {
        object? locator = null, security = null;
        try
        {
            stage = L.T("подключение к root/WMI", "connecting to root/WMI");
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\WMI");
            security = service.Security_;
            ((dynamic)security).ImpersonationLevel = 3;
        }
        finally { Release(security); Release(locator); }
    }

    private string InstancePath(string className)
    {
        if (paths.TryGetValue(className, out var cached)) return cached;
        object? rows = null, row = null, path = null;
        try
        {
            // Synchronous, bidirectional query: real provider HRESULT and no IEnumVARIANT.
            stage = L.T("запрос ", "query ") + className;
            rows = service!.ExecQuery("SELECT * FROM " + className, "WQL", 0);
            if ((int)((dynamic)rows).Count == 0)
                throw new InvalidOperationException(className + L.T(" не вернул экземпляр.", " returned no instance."));
            stage = L.T("экземпляр ", "instance ") + className;
            row = ((dynamic)rows).ItemIndex(0);
            path = ((dynamic)row).Path_;
            var result = (string)((dynamic)path).Path;
            paths.Add(className, result);
            return result;
        }
        finally { Release(path); Release(row); Release(rows); }
    }

    // Вызываем один метод и переводим допустимое число в температуру или null.
    private int? Invoke(string className, string method, string outputName, uint? featureId = null)
    {
        object? definition = null, methods = null, methodDefinition = null, template = null;
        object? input = null, inputProperties = null, idProperty = null;
        object? output = null, properties = null, property = null;
        var path = InstancePath(className);
        try
        {
            stage = className + "." + method;
            if (featureId.HasValue)
            {
                definition = service!.Get(className);
                methods = ((dynamic)definition).Methods_;
                methodDefinition = ((dynamic)methods).Item(method);
                template = ((dynamic)methodDefinition).InParameters;
                input = ((dynamic)template).SpawnInstance_();
                inputProperties = ((dynamic)input).Properties_;
                idProperty = ((dynamic)inputProperties).Item("IDs");
                ((dynamic)idProperty).Value = featureId.Value;
            }
            output = service!.ExecMethod(path, method, input);
            properties = ((dynamic)output).Properties_;
            property = ((dynamic)properties).Item(outputName);
            var value = Convert.ToInt32(((dynamic)property).Value);
            trace.Add($"{className}.{method}{(featureId.HasValue ? $"(0x{featureId:X8})" : "")}: {value}");
            return ValidTemperature(value);
        }
        finally
        {
            Release(property); Release(properties); Release(output);
            Release(idProperty); Release(inputProperties); Release(input);
            Release(template); Release(methodDefinition); Release(methods); Release(definition);
        }
    }

    internal static int? ValidTemperature(int value) => value is > 0 and <= 125 ? value : null;
    internal static bool IsAccessDenied(Exception error) => unchecked((uint)error.HResult) is 0x80041003 or 0x80070005;
    // Различаем Access denied от ошибок провайдера: это помогает не предлагать бесполезное повышение.
    internal static string ExplainError(Exception error, string operation, bool elevated)
    {
        var detail = $"{operation}: {error.Message} (0x{unchecked((uint)error.HResult):X8}).";
        if (IsAccessDenied(error))
            return elevated ? L.T("Lenovo WMI отклонил доступ даже с правами администратора. ", "Lenovo WMI denied access even with administrator privileges. ") + detail
                : L.T("Для датчиков Lenovo нужны права администратора. Нажмите «Перезапустить от администратора». ", "Lenovo sensors require administrator privileges. Click “Restart as administrator”. ") + detail;
        return detail + (elevated ? L.T(" Процесс: администратор.", " Process: administrator.") : L.T(" Процесс: обычные права.", " Process: standard privileges."));
    }

    // Резервный путь пробуется лишь после основного; запрет доступа не скрываем за второй ошибкой.
    private (int? Value, string Source, string? Error) ReadSensor(uint featureId, string legacyMethod)
    {
        var errors = new List<string>();
        try
        {
            var value = Invoke("LENOVO_OTHER_METHOD", "GetFeatureValue", "value", featureId);
            if (value.HasValue) return (value, "Lenovo WMI3", null);
        }
        catch (Exception e)
        {
            if (IsAccessDenied(e)) throw;
            errors.Add(ExplainError(e, stage, IsAdministrator));
        }
        try
        {
            var value = Invoke("LENOVO_GAMEZONE_DATA", legacyMethod, "Data");
            if (value.HasValue) return (value, "Lenovo Gamezone", null);
        }
        catch (Exception e)
        {
            if (IsAccessDenied(e)) throw;
            errors.Add(ExplainError(e, stage, IsAdministrator));
        }
        return (null, "—", errors.Count > 0 ? string.Join("\n", errors) : L.T("Прошивка не вернула допустимую температуру (—).", "Firmware did not return a valid temperature (—)."));
    }

    // Датчики читаются последовательно в одном COM-сеансе; ошибки превращаются в диагностический снимок.
    public Reading Poll()
    {
        trace.Clear();
        try
        {
            if (service == null) Connect();
            var cpu = ReadSensor(CpuTemperatureId, "GetCPUTemp");
            var gpu = ReadSensor(GpuTemperatureId, "GetGPUTemp");
            var errors = new[] { cpu.Error, gpu.Error }.Where(x => x != null).Distinct().ToArray();
            if (errors.Length > 0 && cpu.Value == null && gpu.Value == null) Dispose();
            return new(cpu.Value, gpu.Value, errors.Length > 0 ? string.Join("\n", errors)
                : $"CPU: {cpu.Source} · GPU: {gpu.Source} · {(IsAdministrator ? L.T("администратор", "administrator") : L.T("обычные права", "standard privileges"))}");
        }
        catch (Exception e)
        {
            Dispose();
            return new(null, null, ExplainError(e, stage, IsAdministrator));
        }
    }

    // Служебный запуск пишет локальный JSON. Рабочая GUI-копия при этом может продолжать работу.
    public static string Diagnose()
    {
        using var reader = new SensorReader();
        var reading = reader.Poll();
        return JsonSerializer.Serialize(new { IsAdministrator, Reading = reading, Calls = reader.trace }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public void Dispose() { Release(service); service = null; paths.Clear(); }
}
