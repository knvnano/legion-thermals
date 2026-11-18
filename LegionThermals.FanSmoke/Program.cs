using LegionThermals;
using System.Text.Json;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var log = new List<object>();
        var passed = false;
        using var wmi = new WmiSession();
        using var fans = new FanController();
        using var reader = new SensorReader();
        int Mode() => Convert.ToInt32(wmi.Call("LENOVO_GAMEZONE_DATA", "GetSmartFanMode", null, "Data")["Data"]);
        int Feature(uint id) => Convert.ToInt32(wmi.Call("LENOVO_OTHER_METHOD", "GetFeatureValue", new() { ["IDs"] = id }, "value")["value"]);
        int[] Curve() => ((Array)wmi.Call("LENOVO_FAN_METHOD", "Fan_Get_Table", new() { ["FanID"] = (byte)0, ["SensorID"] = (byte)0 }, "FanTable")["FanTable"]!).Cast<object>().Select(Convert.ToInt32).Take(10).ToArray();
        void Save() => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fan-smoke.json"), JsonSerializer.Serialize(new { Passed = passed, Log = log }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        int? mode = null, full = null; int[]? curve = null;
        try
        {
            if (args.Contains("--apply")) fans.Update(false, 40, reader.Poll()); // Recover a journal left by an older app first.
            mode = Mode(); full = Feature(0x04020000); curve = Curve();
            log.Add(new { BeforeMode = mode, BeforeFullSpeed = full, BeforeCurve = curve, Probe = fans.Probe() }); Save();
            if (args.Contains("--apply"))
            {
                var settled = new List<(int Cpu, int Gpu)>();
                foreach (var percent in new[] { 40, 80, 40 })
                {
                    FanSnapshot? snapshot = null;
                    for (var second = 0; second < 20; second++)
                    {
                        snapshot = fans.Update(true, percent, reader.Poll());
                        if (!snapshot.Supported || snapshot.ManualStopped || Feature(0x04020000) != 0) throw new Exception("Manual curve is overridden or unavailable");
                        if (second % 5 == 0) { log.Add(new { Percent = percent, Second = second, Snapshot = snapshot, Mode = Mode(), FullSpeed = Feature(0x04020000), Curve = Curve() }); Save(); }
                        Thread.Sleep(1000);
                    }
                    if (snapshot?.CpuActualRpm is not int cpu || snapshot.GpuActualRpm is not int gpu || cpu < 500 || gpu < 500) throw new Exception("Valid measured RPM required");
                    settled.Add((cpu, gpu));
                }
                if (settled[1].Cpu - settled[0].Cpu < 500 || settled[1].Gpu - settled[0].Gpu < 500 || settled[1].Cpu - settled[2].Cpu < 500 || settled[1].Gpu - settled[2].Gpu < 500)
                    throw new Exception("Measured RPM did not respond to 40 -> 80 -> 40 percent");
            }
            passed = true;
        }
        catch (Exception e) { log.Add(new { Error = e.ToString() }); }
        finally
        {
            try
            {
                fans.Restore();
                log.Add(new { RestoredMode = Mode(), RestoredFullSpeed = Feature(0x04020000), RestoredCurve = Curve() });
                if (mode != null && (Mode() != mode || Feature(0x04020000) != full || !Curve().SequenceEqual(curve!))) throw new Exception("Restore mismatch");
            }
            catch (Exception e) { passed = false; log.Add(new { RestoreError = e.ToString() }); }
            Save();
        }
        return passed ? 0 : 1;
    }
}
