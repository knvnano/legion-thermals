// Единственный слой, который записывает настройки охлаждения в Lenovo WMI.
// Reading — измеренные температуры; FanSnapshot — результат для интерфейса;
// FanBaseline — состояние ноутбука ДО вмешательства программы, нужное для отката.
// Все вызовы этого объекта выполняет один аппаратный поток, без параллельных записей.
// 
// Последовательность ручного управления:
//   1. Проверить модель и допустимость таблиц.
//   2. Сохранить режим, кривую и флаг full-speed на диск.
//   3. Включить Lenovo Custom и записать допустимую кривую.
//   4. Сбросить отдельный принудительный максимум и проверить обратным чтением.
// При ошибке, Auto или выходе выполняется Restore. Журнал удаляется только
// после успешного восстановления. Форсированное завершение процесса обходит finally;
// поэтому журнал подхватывается при следующем запуске.
// Проценты здесь означают НИЖНЮЮ ГРАНИЦУ кривой, а не предел оборотов.

using System.Text.Json;

namespace LegionThermals;

internal sealed record FanSnapshot(bool Supported, string Status, int? CpuTargetRpm = null, int? GpuTargetRpm = null, bool ManualStopped = false, int? CpuActualRpm = null, int? GpuActualRpm = null);
internal sealed record FanBaseline(int Mode, int[] Levels, int? FullSpeed = null);

// One worker owns all calls. The slider raises a firmware-curve floor, never caps cooling.
internal sealed class FanController : IDisposable
{
    private readonly WmiSession wmi = new();
    private FanBaseline? baseline;
    private int[]? lastApplied;
    private int[] cpuLadder = [], gpuLadder = [];
    private bool probed, recovered;
    private string? unsupported;
    private static string Journal => Path.Combine(Settings.Folder, "fan-session.json");
    private const uint FullSpeedId = 0x04020000;

    // Чтение одного числового свойства: IDs — входной параметр, value — выходной.
    private int Feature(uint id) => Convert.ToInt32(wmi.Call("LENOVO_OTHER_METHOD", "GetFeatureValue", new() { ["IDs"] = id }, "value")["value"]);
    // Полный максимум — отдельный флаг FNST, который способен перекрыть всю кривую.
    private int FullSpeed()
    {
        var value = Feature(FullSpeedId);
        if (value is not (0 or 1)) throw new InvalidOperationException(L.T("Неизвестное значение принудительного максимума Lenovo.", "Unknown Lenovo full-speed override value."));
        return value;
    }
    // После записи проверяем статус, а не полагаемся на отсутствие COM-исключения.
    private void SetFullSpeed(int value)
    {
        wmi.Call("LENOVO_OTHER_METHOD", "SetFeatureValue", new() { ["IDs"] = FullSpeedId, ["value"] = (uint)value });
        if (FullSpeed() != value) throw new InvalidOperationException(L.T("Lenovo не подтвердил изменение принудительного максимума.", "Lenovo did not confirm the full-speed override change."));
    }
    // Сбой тахометра не должен мешать возврату штатного охлаждения.
    private FanSnapshot WithRpm(FanSnapshot snapshot)
    {
        // Telemetry failure must not prevent Auto or restoration of cooling.
        int? Read(uint id) { try { var rpm = Feature(id); return rpm is >= 0 and <= 15000 ? rpm : null; } catch { return null; } }
        return snapshot with { CpuActualRpm = Read(0x04030001), GpuActualRpm = Read(0x04030002) };
    }

    private static int[] Numbers(object? value) => value is Array a
        ? a.Cast<object>().Select(Convert.ToInt32).ToArray()
        : throw new InvalidOperationException(L.T("WMI не вернул массив.", "WMI did not return an array."));

    private int Mode() => Convert.ToInt32(wmi.Call("LENOVO_GAMEZONE_DATA", "GetSmartFanMode", null, "Data")["Data"]);
    private void SetMode(int mode)
    {
        wmi.Call("LENOVO_GAMEZONE_DATA", "SetSmartFanMode", new() { ["Data"] = (uint)mode });
        if (Mode() != mode) throw new InvalidOperationException(L.T("Lenovo не подтвердил смену режима охлаждения.", "Lenovo did not confirm the cooling mode change."));
    }

    // Получаем десять уровней прошивки. FanID=0/1 проверяются отдельно.
    private int[] Curve(byte fan = 0)
    {
        var output = wmi.Call("LENOVO_FAN_METHOD", "Fan_Get_Table",
            new() { ["FanID"] = fan, ["SensorID"] = (byte)0 }, "FanTable", "FanTableSize");
        var values = Numbers(output["FanTable"]);
        if (Convert.ToInt32(output["FanTableSize"]) != 10 || values.Length < 10)
            throw new InvalidOperationException(L.T("Неподдерживаемая длина таблицы вентилятора.", "Unsupported fan table length."));
        values = values.Take(10).ToArray();
        FanPolicy.ValidateLevels(values);
        return values;
    }

    // Записываем пакет и подтверждаем обратным чтением обоих представлений вентилятора.
    private void WriteCurve(int[] values)
    {
        wmi.Call("LENOVO_FAN_METHOD", "Fan_Set_Table", new() { ["FanTable"] = FanPolicy.Encode(values) });
        if (!Curve().SequenceEqual(values) || !Curve(1).SequenceEqual(values))
            throw new InvalidOperationException(L.T("Прошивка не подтвердила таблицу вентиляторов.", "Firmware did not confirm the fan curve."));
    }

    // Только проверенная модель/семейство BIOS. Для неизвестного оборудования записи отключены.
    public FanSnapshot Probe()
    {
        if (!probed)
        {
            if (!SensorReader.IsAdministrator) return new(false, L.T("Для управления вентиляторами запустите программу от администратора.", "Run as administrator to control the fans."));
            using var system = new WmiSession(@"root\cimv2");
            var model = Convert.ToString(system.Query("Win32_ComputerSystem", "Model").Single()["Model"]);
            var bios = Convert.ToString(system.Query("Win32_BIOS", "SMBIOSBIOSVersion").Single()["SMBIOSBIOSVersion"]);
            if (model != "83DF" || bios?.StartsWith("N0CN", StringComparison.OrdinalIgnoreCase) != true)
                unsupported = L.T("Ручное управление проверено только для Lenovo 83DF с BIOS N0CN; на этой модели отключено.", "Manual control is validated only on Lenovo 83DF / N0CN BIOS; disabled on this model.");
            else
            {
                var rows = wmi.Query("LENOVO_FAN_TABLE_DATA", "Mode", "Fan_Id", "FanTable_Data");
                int[] Ladder(int fan) => Numbers(rows.Where(r => Convert.ToInt32(r["Fan_Id"]) == fan)
                    .OrderByDescending(r => Convert.ToInt32(r["Mode"]) == 255).First()["FanTable_Data"]);
                cpuLadder = Ladder(1); gpuLadder = Ladder(2);
                FanPolicy.ValidateLadder(cpuLadder); FanPolicy.ValidateLadder(gpuLadder);
                var curve = Curve();
                if (!curve.SequenceEqual(Curve(1))) throw new InvalidOperationException(L.T("Несогласованные таблицы двух вентиляторов.", "The two fans returned inconsistent curves."));
            }
            probed = true;
        }
        return new(unsupported == null, unsupported ?? L.T("Авто Lenovo. Ползунок задаёт минимальную скорость обоих вентиляторов.", "Lenovo Auto. The slider sets a minimum speed for both fans."));
    }

    // Разовый откат журнала прошлой аварийной сессии до начала нового ручного управления.
    private void RecoverPreviousSession()
    {
        if (recovered) return;
        if (File.Exists(Journal))
        {
            baseline = JsonSerializer.Deserialize<FanBaseline>(File.ReadAllText(Journal))
                ?? throw new InvalidOperationException(L.T("Не удалось прочитать резервную таблицу вентилятора.", "Could not read the fan recovery curve."));
            ValidateBaseline(baseline);
            Restore();
        }
        recovered = true;
    }

    // Проверяем и текущий журнал, и журналы старой версии с отсутствующим FullSpeed.
    private static void ValidateBaseline(FanBaseline saved)
    {
        FanPolicy.ValidateLevels(saved.Levels);
        if (saved.FullSpeed is not (null or 0 or 1)) throw new InvalidOperationException(L.T("Неизвестное сохранённое состояние принудительного максимума.", "Unknown saved full-speed override state."));
        if (saved.Mode is not (1 or 2 or 3 or 255)) throw new InvalidOperationException(L.T("Неизвестный сохранённый режим Lenovo.", "Unknown saved Lenovo mode."));
    }

    // Один шаг управления. Внешняя смена режима считается намерением пользователя и выключает управление.
    public FanSnapshot Update(bool manual, int percent, Reading temperature)
    {
        var support = Probe();
        if (!support.Supported) return support;
        RecoverPreviousSession();
        if (baseline != null && lastApplied != null && Mode() != 255)
        {
            Restore();
            return new(true, L.T("Режим Lenovo изменён извне: ручной минимум отключён.", "Lenovo mode changed externally: manual control was disabled."), ManualStopped: true);
        }
        if (!manual)
        {
            if (baseline != null) Restore();
            return WithRpm(support);
        }
        if (temperature.Cpu == null || temperature.Gpu == null)
        {
            if (baseline != null) Restore();
            return new(true, L.T("Нет надёжных температур: возвращён режим Авто Lenovo.", "Reliable temperatures unavailable: restored Lenovo Auto."));
        }
        try
        {
            if (baseline == null)
            {
                var saved = new FanBaseline(Mode(), Curve(), FullSpeed());
                ValidateBaseline(saved);
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(Journal + ".tmp", JsonSerializer.Serialize(saved));
                File.Move(Journal + ".tmp", Journal, true);
                baseline = saved;
            }
            if (Mode() != 255) SetMode(255);
            var hot = temperature.Cpu >= 90 || temperature.Gpu >= 80;
            var desired = FanPolicy.BuildLevels(baseline.Levels, cpuLadder, gpuLadder, percent, hot);
            if (lastApplied == null || !lastApplied.SequenceEqual(desired)) WriteCurve(desired);
            // N0CN32WW: FNST=1 overrides the whole F9F0..F9F9 curve in Custom.
            // Write a valid curve before releasing the override, then verify it.
            if (FullSpeed() != 0) SetFullSpeed(0);
            lastApplied = desired;
            var floor = desired[0] - 1;
            return WithRpm(new(true, hot ? L.T("Высокая температура: включён максимальный уровень охлаждения.", "High temperature: maximum cooling curve enabled.")
                : L.T($"Ручной минимум {FanPolicy.ClampPercent(percent)}%. Lenovo Custom; при нагреве обороты растут автоматически.", $"Manual minimum {FanPolicy.ClampPercent(percent)}%. Lenovo Custom; firmware may increase speed as temperatures rise."),
                cpuLadder[floor], gpuLadder[floor]));
        }
        catch
        {
            if (baseline != null) Restore();
            throw;
        }
    }

    // Возвращаем все сохранённые параметры. Если один шаг не удался, журнал остаётся для повторной попытки.
    public void Restore()
    {
        if (baseline == null) return;
        ValidateBaseline(baseline);
        var current = Mode();
        var returnMode = current == 255 ? baseline.Mode : current;
        if (current != 255) SetMode(255);
        try { WriteCurve(baseline.Levels); }
        finally
        {
            try { if (returnMode != 255) SetMode(returnMode); }
            finally { if (baseline.FullSpeed is int original && FullSpeed() != original) SetFullSpeed(original); }
        }
        baseline = null; lastApplied = null;
        if (File.Exists(Journal)) File.Delete(Journal);
    }

    public void Dispose() => wmi.Dispose();
}

