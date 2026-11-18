// Чистые расчёты: этот класс ничего не знает о WMI, окнах и файлах.
// Lenovo хранит десять УРОВНЕЙ скорости, а не проценты PWM.
// Ladder — таблица соответствия уровня реальным целевым об/мин для каждого вентилятора.
// BuildLevels округляет запрос вверх: минимум должен выполняться для ОБОИХ вентиляторов.
// Исходные точки нельзя уменьшать; верхние точки сохраняют усиленное охлаждение.
// Encode упаковывает результат в 64 байта протокола. Это не машинный код:
// байты — значения параметров, которые получит штатный провайдер Lenovo.

namespace LegionThermals;

internal static class FanPolicy
{
    public const int MinimumPercent = 30;
    public static int ClampPercent(int value) => Math.Clamp(value, MinimumPercent, 100);

    // Отклоняем числа, которые прошивка может интерпретировать иначе, чем уровни 1–10.
    public static void ValidateLevels(int[] levels)
    {
        if (levels.Length != 10 || levels.Any(x => x is < 1 or > 10))
            throw new InvalidOperationException(L.T("Неподдерживаемая таблица: нужны 10 уровней 1–10.", "Unsupported curve: ten levels in the range 1–10 are required."));
    }

    // Проверяем масштаб RPM, чтобы не отправить проценты в интерфейс с другими единицами.
    public static void ValidateLadder(int[] ladder)
    {
        if (ladder.Length != 10 || ladder[0] < 500 || ladder[^1] > 10000 ||
            ladder.Zip(ladder.Skip(1), (a, b) => b > a).Any(ok => !ok))
            throw new InvalidOperationException(L.T("Неизвестная шкала оборотов Lenovo.", "Unknown Lenovo fan RPM scale."));
    }

    // Не меняем входной массив: новая кривая создаётся отдельно для сравнения/повторного применения.
    public static int[] BuildLevels(int[] baseline, int[] cpuLadder, int[] gpuLadder, int percent, bool hot)
    {
        ValidateLevels(baseline); ValidateLadder(cpuLadder); ValidateLadder(gpuLadder);
        percent = ClampPercent(percent);
        int Level(int[] ladder) => Array.FindIndex(ladder, rpm => rpm >= ladder[^1] * percent / 100.0) + 1;
        var floor = hot ? 10 : Math.Max(Level(cpuLadder), Level(gpuLadder));
        var levels = baseline.Select(x => Math.Max(x, floor)).ToArray();
        for (var i = 1; i < levels.Length; i++) levels[i] = Math.Max(levels[i], levels[i - 1]);
        levels[8] = Math.Max(levels[8], 9);
        levels[9] = 10; // retain full cooling at the hot end, including low slider settings
        return levels;
    }

    // Поля уровней находятся в смещениях 6, 8, …24; остальные байты остаются нулевыми.
    public static byte[] Encode(int[] levels)
    {
        ValidateLevels(levels);
        var payload = new byte[64];
        payload[0] = 1; // Lenovo FSTM; FSID/FSTL remain zero
        for (var i = 0; i < 10; i++) payload[6 + 2 * i] = (byte)levels[i];
        return payload;
    }
}
