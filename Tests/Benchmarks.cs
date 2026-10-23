using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Powder;

// Headless, deterministic workloads. Timings are observations, never test assertions.
public static class Benchmarks
{
    private const int WarmupSteps = 30, MeasuredSteps = 180;

    public static int Run(string[] args)
    {
        string? Option(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Size(string name, int fallback, out int value)
        {
            string? raw = Option(name);
            value = fallback;
            return raw is null || int.TryParse(raw, out value) && value is >= 96 and <= 1024;
        }
        if (!Size("--benchmark-width", GameDefaults.Width, out int width)
            || !Size("--benchmark-height", GameDefaults.Height, out int height))
        {
            Console.Error.WriteLine("Benchmark dimensions must be integers between 96 and 1024.");
            return 2;
        }
        string? selected = Option("--benchmark-scene");
        string[] scenarios = selected is null ? ["empty", "water-boiling", "demo"] : [selected];
        if (scenarios.Any(s => s is not ("empty" or "water-boiling" or "demo")))
        {
            Console.Error.WriteLine("Benchmark scene must be empty, water-boiling or demo.");
            return 2;
        }
        var measurements = new List<Measurement>();
        foreach (string scenario in scenarios)
        {
            var world = new World(width, height);
            if (scenario == "demo") Scenes.Demo(world);
            if (scenario == "water-boiling") BoilingTank(world);
            for (int n = 0; n < WarmupSteps; n++) Advance(world, scenario);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
            long allocatedStart = GC.GetTotalAllocatedBytes(true);
            var samples = new double[MeasuredSteps];
            var elapsed = Stopwatch.StartNew();
            for (int n = 0; n < MeasuredSteps; n++)
            {
                long start = Stopwatch.GetTimestamp();
                Advance(world, scenario);
                samples[n] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            elapsed.Stop(); process.Refresh();
            double cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedStart;
            Array.Sort(samples);
            var stats = world.Statistics();
            var measurement = new Measurement(scenario, WarmupSteps, MeasuredSteps,
                elapsed.Elapsed.TotalMilliseconds / MeasuredSteps, cpu / MeasuredSteps,
                samples[MeasuredSteps / 2], samples[(int)(MeasuredSteps * .95)],
                allocated / MeasuredSteps, stats.Particles, stats.Mass, stats.Energy,
                world.Cells.Count(p => p.Element is Element.Steam or Element.Vapor)
                    + world.Bubbles.Count(p => p.Element is Element.Steam or Element.Vapor),
                scenario == "water-boiling" ? SurfaceBowlDepth(world) : null,
                world.Air.ControlVolumeCount, world.Air.OpenFaceCount);
            measurements.Add(measurement);
            Console.WriteLine($"{scenario}: {measurement.WallMillisecondsPerStep:F3} ms/step wall, "
                + $"{measurement.CpuMillisecondsPerStep:F3} ms/step CPU, "
                + $"p95 {measurement.P95MillisecondsPerStep:F3} ms, {measurement.Particles} particles");
        }
        var report = new
        {
            FormatVersion = 2,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Width = width, Height = height, FixedDelta = World.FixedDelta,
            Measurements = measurements
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        string? output = Option("--benchmark-output");
        if (output is not null)
        {
            string path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json + Environment.NewLine);
            Console.WriteLine($"Benchmark report: {path}");
        }
        else Console.WriteLine(json);
        return 0;
    }

    private static void Advance(World world, string scenario)
    {
        if (scenario == "water-boiling")
        {
            var scene = new SceneLayout(world);
            world.HeatBrush(scene.X(240), scene.Y(301), scene.Radius(10), 20);
        }
        world.Step();
    }

    private static void BoilingTank(World w)
    {
        var scene = new SceneLayout(w);
        scene.Rect(164, 198, 4, 110, Element.Glass);
        scene.Rect(312, 198, 4, 110, Element.Glass);
        scene.Rect(164, 304, 152, 4, Element.Glass);
        scene.Rect(168, 208, 144, 96, Element.Water, 99);
        scene.Rect(228, 299, 24, 5, Element.Copper, 250);
    }

    // Positive values indicate a depressed centre relative to both outer quarters.
    // Kept as a diagnostic: boiling legitimately disturbs the surface, so tests should
    // measure a time average after heating stops instead of requiring an exactly flat frame.
    private static double? SurfaceBowlDepth(World w)
    {
        double centre = 0, edges = 0; int centreCount = 0, edgeCount = 0;
        var scene = new SceneLayout(w);
        for (int x = scene.X(176); x < scene.X(304); x++)
        {
            int surface = -1;
            for (int y = 0; y < w.Height; y++)
                if (w.At(x, y).Element == Element.Water) { surface = y; break; }
            if (surface < 0) continue;
            if (x >= scene.X(216) && x < scene.X(264)) { centre += surface; centreCount++; }
            if (x < scene.X(200) || x >= scene.X(280)) { edges += surface; edgeCount++; }
        }
        return centreCount > 0 && edgeCount > 0 ? centre / centreCount - edges / edgeCount : null;
    }

    private sealed record Measurement(string Scenario, int WarmupSteps, int MeasuredSteps,
        double WallMillisecondsPerStep, double CpuMillisecondsPerStep,
        double MedianMillisecondsPerStep, double P95MillisecondsPerStep,
        long AllocatedBytesPerStep, int Particles, double Mass, double Energy,
        int VaporParticles, double? SurfaceBowlDepthCells, int AirVolumes, int AirOpenFaces);
}
