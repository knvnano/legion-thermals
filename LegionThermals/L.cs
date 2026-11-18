namespace LegionThermals;

/// <summary>
/// Маленький словарь интерфейса без дополнительных библиотек и файлов ресурсов.
/// В каждом вызове T рядом видны русский оригинал и английский перевод.
/// Язык читают и UI, и аппаратный поток; Volatile гарантирует видимость смены.
/// Технические имена WMI, ключи JSON и команды никогда не переводятся.
/// </summary>
internal static class L
{
    private static string language = "ru";
    public static string Language
    {
        get => Volatile.Read(ref language);
        set => Volatile.Write(ref language, value == "en" ? "en" : "ru");
    }
    public static string T(string russian, string english) => Language == "en" ? english : russian;
}
