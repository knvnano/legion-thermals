// Обёртка над встроенным Windows COM API WbemScripting, без System.Management.
// WMI — системная служба: классы Lenovo описывают доступные методы прошивки.
// COM-объекты живут вне управляемой памяти .NET, поэтому требуют явного Release.
// Мы освобождаем свойства, экземпляры и коллекции в finally даже при ошибках.
// Session создаётся и используется на одном STA-потоке; передавать её в UI нельзя.
// Синхронный ExecQuery с ItemIndex сохраняет реальный HRESULT провайдера и
// обходит проблемное COM-перечисление IEnumVARIANT на проверенном ноутбуке.

using System.Runtime.InteropServices;

namespace LegionThermals;

// Windows COM only. Every session is created, used and disposed on the same worker.
internal sealed class WmiSession(string scope = @"root\WMI") : IDisposable
{
    private dynamic? service;
    private readonly Dictionary<string, string> paths = new();
    private dynamic Service
    {
        get
        {
            if (service != null) return service;
            object? locator = null, security = null;
            try
            {
                locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
                service = ((dynamic)locator!).ConnectServer(".", scope);
                security = service.Security_;
                ((dynamic)security).ImpersonationLevel = 3;
                return service;
            }
            finally { Release(security); Release(locator); }
        }
    }

    // Проверка IsComObject нужна потому, что часть результатов — обычные числа/массивы .NET.
    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    // Промежуточные COM-объекты Properties/Property тоже необходимо освободить.
    private static object? Property(object target, string name)
    {
        object? properties = null, property = null;
        try
        {
            properties = ((dynamic)target).Properties_;
            property = ((dynamic)properties).Item(name);
            return ((dynamic)property).Value;
        }
        finally { Release(property); Release(properties); }
    }

    // Копируем значения из COM в обычные словари. После возврата COM-строки уже освобождены.
    public List<Dictionary<string, object?>> Query(string className, params string[] fields)
    {
        object? rows = null;
        var result = new List<Dictionary<string, object?>>();
        try
        {
            rows = Service.ExecQuery("SELECT * FROM " + className, "WQL", 0);
            var count = (int)((dynamic)rows).Count;
            if (count > 256) throw new InvalidOperationException(L.T("Неожиданно большая таблица WMI.", "Unexpectedly large WMI table."));
            for (var i = 0; i < count; i++)
            {
                object? row = null;
                try
                {
                    row = ((dynamic)rows).ItemIndex(i);
                    result.Add(fields.ToDictionary(f => f, f => Property(row!, f)));
                }
                finally { Release(row); }
            }
            return result;
        }
        finally { Release(rows); }
    }

    // Метод WMI вызывается у конкретного экземпляра, поэтому кешируем полный путь к нему.
    private string InstancePath(string className)
    {
        if (paths.TryGetValue(className, out var path)) return path;
        object? rows = null, row = null, objectPath = null;
        try
        {
            rows = Service.ExecQuery("SELECT * FROM " + className, "WQL", 0);
            if ((int)((dynamic)rows).Count < 1) throw new InvalidOperationException(className + L.T(" не вернул экземпляр.", " returned no instance."));
            row = ((dynamic)rows).ItemIndex(0);
            objectPath = ((dynamic)row).Path_;
            path = (string)((dynamic)objectPath).Path;
            paths.Add(className, path);
            return path;
        }
        finally { Release(objectPath); Release(row); Release(rows); }
    }

    // Из описания метода создаём входной объект нужной формы; параметры не конструируются вручную.
    public Dictionary<string, object?> Call(string className, string method,
        Dictionary<string, object?>? arguments = null, params string[] outputs)
    {
        object? definition = null, methods = null, methodDefinition = null, template = null;
        object? input = null, properties = null, output = null;
        try
        {
            var path = InstancePath(className);
            if (arguments?.Count > 0)
            {
                definition = Service.Get(className);
                methods = ((dynamic)definition).Methods_;
                methodDefinition = ((dynamic)methods).Item(method);
                template = ((dynamic)methodDefinition).InParameters;
                input = ((dynamic)template).SpawnInstance_();
                properties = ((dynamic)input).Properties_;
                foreach (var (name, value) in arguments)
                {
                    object? property = null;
                    try { property = ((dynamic)properties).Item(name); ((dynamic)property).Value = value; }
                    finally { Release(property); }
                }
            }
            output = input == null ? Service.ExecMethod(path, method) : Service.ExecMethod(path, method, input);
            return outputs.ToDictionary(f => f, f => Property(output!, f));
        }
        finally
        {
            Release(output); Release(properties); Release(input); Release(template);
            Release(methodDefinition); Release(methods); Release(definition);
        }
    }

    public void Dispose() { Release(service); service = null; paths.Clear(); }
}
