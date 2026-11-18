// Температурный планировщик без доступа к оборудованию.
// Точка таблицы: начиная с Temperature градусов просить не меньше Percent процентов.
// Validate проверяет таблицу, Select выбирает ступень по уже отсортированным точкам.
// active — индекс последней активной ступени; -1 означает температуру ниже первой.
// Гистерезис в 3 градуса не даёт оборотам прыгать из-за колебаний на границе.
// При нагреве задержки нет. При потере датчиков/замене таблицы вызывается Reset.

namespace LegionThermals;

public sealed record FanSchedulePoint(int Temperature, int Percent);
internal sealed record FanScheduleConfig(bool Enabled, FanSchedulePoint[] Points);

// No hardware access: the worker converts a temperature into a requested floor.
internal sealed class FanSchedule
{
    private int active = -1;
    public static FanSchedulePoint[] Defaults() => new[] { 40, 50, 60, 65, 70, 75, 80, 85 }
        .Select((temperature, i) => new FanSchedulePoint(temperature, 30 + i * 10)).ToArray();

    // null означает успешную проверку; иначе возвращается понятное сообщение для редактора.
    public static string? Validate(FanSchedulePoint[]? points)
    {
        if (points is not { Length: >= 1 and <= 16 }) return L.T("Нужны от 1 до 16 строк.", "The table must contain 1 to 16 rows.");
        if (points.Any(p => p == null || p.Temperature is < 20 or > 100 || p.Percent is < 30 or > 100 || p.Percent % 10 != 0))
            return L.T("Температура: 20–100 °C; скорость: 30–100% с шагом 10%.", "Temperature: 20–100 °C; speed: 30–100% in steps of 10%.");
        var ordered = points.OrderBy(p => p.Temperature).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Temperature == ordered[i - 1].Temperature) return L.T("Температуры в строках должны различаться.", "Each row must have a different temperature threshold.");
            if (ordered[i].Percent < ordered[i - 1].Percent) return L.T("При повышении температуры скорость не должна уменьшаться.", "Fan speed must not decrease as temperature increases.");
        }
        return null;
    }

    public void Reset() => active = -1;
    // sorted уже проверен и отсортирован при публикации конфигурации; лишних выделений памяти тут нет.
    public int Select(FanSchedulePoint[] sorted, int temperature)
    {
        var desired = Array.FindLastIndex(sorted, p => temperature >= p.Temperature);
        if (desired >= active) active = desired; // accelerate immediately at a threshold
        else if (active >= 0 && temperature < sorted[active].Temperature - 3) active = desired;
        return active < 0 ? 30 : sorted[active].Percent;
    }
}
