using System.Diagnostics;

namespace Powder;

public static class SelfTests
{
    private static int passed, failed;

    public static int Run()
    {
        Test("material data uses SI units", () =>
        {
            Check(Materials.Get(Element.Water).SpecificHeat == 4184, "water heat capacity");
            Check(Materials.Get(Element.Copper).Conductivity > Materials.Get(Element.Wood).Conductivity * 1000,
                "copper / wood conductivity contrast");
        });
        Test("gravity moves sand and conserves mass", () =>
        {
            var w = new World(32, 32) { AmbientCooling = false };
            w.Set(10, 2, Element.Sand);
            double mass = w.Statistics().Mass;
            for (int n = 0; n < 90; n++) w.Step();
            Check(w.Cells.Select((p, i) => (p, i)).Single(p => p.p.Element == Element.Sand).i / w.Width > 25,
                "sand did not fall");
            Near(w.Statistics().Mass, mass, 1e-15);
        });
        Test("sand sinks through liquid by density", () =>
        {
            var w = Tube();
            for (int y = 4; y < 18; y++) w.Set(8, y, Element.Water);
            w.Set(8, 3, Element.Sand);
            for (int n = 0; n < 100; n++) w.Step();
            Check(w.At(8, 17).Element == Element.Sand, "sand failed to displace water");
        });
        Test("oil rises through water", () =>
        {
            var w = Tube();
            for (int y = 4; y < 18; y++) w.Set(8, y, Element.Water);
            w.Set(8, 17, Element.Oil);
            for (int n = 0; n < 120; n++) w.Step();
            Check(w.At(8, 4).Element == Element.Oil, "oil failed to rise");
        });
        Test("liquid spreads into an open basin", () =>
        {
            var w = new World(48, 24) { AmbientCooling = false };
            Scenes.Rect(w, 0, 22, 48, 2, Element.Stone);
            Scenes.Rect(w, 2, 10, 7, 12, Element.Water);
            for (int n = 0; n < 180; n++) w.Step();
            Check(w.Cells.Where((_, i) => i % w.Width > 30).Any(p => p.Element == Element.Water),
                "liquid did not level");
        });
        Test("heat exchange conserves energy and approaches equilibrium", () =>
        {
            Particle a = Make(Element.Copper, 400), b = Make(Element.Iron, 20);
            double energy = a.Energy + b.Energy;
            for (int n = 0; n < 100; n++) Thermodynamics.Exchange(ref a, ref b, World.FixedDelta);
            Near(a.Energy + b.Energy, energy, 1e-10);
            Check(Math.Abs(a.Temperature - b.Temperature) < .001, "no thermal equilibrium");
            Check(a.Temperature > 20 && a.Temperature < 400, "exchange overshot");
        });
        Test("specific heat controls temperature response", () =>
        {
            Particle water = Make(Element.Water, 20), copper = Make(Element.Copper, 20);
            double energyPerKg = 1000;
            water.Energy += water.Mass * energyPerKg;
            copper.Energy += copper.Mass * energyPerKg;
            Check(copper.Temperature - 20 > (water.Temperature - 20) * 10, "capacity ignored");
        });
        Test("conductivity changes heat transfer rate", () =>
        {
            Particle copper = Make(Element.Copper, 100), wood = Make(Element.Wood, 100);
            Particle a = Make(Element.Water, 20), b = Make(Element.Water, 20);
            Thermodynamics.Exchange(ref copper, ref a, .001);
            Thermodynamics.Exchange(ref wood, ref b, .001);
            Check(a.Temperature > b.Temperature, "conductivity ignored");
        });
        Test("ice consumes latent heat before melting", () =>
        {
            var p = Make(Element.Ice, 0); double mass = p.Mass;
            p.Energy = mass * Thermodynamics.Fusion * .5;
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Ice, "premature melting"); Near(p.Temperature, 0, 1e-12);
            p.Energy = mass * (Thermodynamics.Fusion + 4184 * 10);
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Water, "ice did not melt");
            Near(p.Temperature, 10, 1e-10); Near(p.Mass, mass, 1e-15);
        });
        Test("boiling and condensation pay latent heat", () =>
        {
            var p = Make(Element.Water, 100); double mass = p.Mass;
            p.Energy = mass * (Thermodynamics.BoilingLiquid + Thermodynamics.Vaporization * .5);
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Water, "premature evaporation"); Near(p.Temperature, 100, 1e-10);
            p.Energy = mass * (Thermodynamics.BoilingGas + 1996 * 20);
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Steam, "no steam"); Near(p.Temperature, 120, 1e-10);
            p.Energy = mass * (Thermodynamics.BoilingGas - 10);
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Water, "no condensation"); Near(p.Mass, mass, 1e-15);
        });
        Test("thermal brush respects absolute zero", () =>
        {
            var w = new World(16, 16);
            w.Set(8, 8, Element.Copper);
            w.HeatBrush(8, 8, 1, -10000);
            Near(w.At(8, 8).Temperature, -273.15, 1e-8);
            w.Set(8, 8, Element.Water);
            w.HeatBrush(8, 8, 1, -10000);
            Near(w.At(8, 8).Temperature, -273.15, 1e-8);
        });
        Test("metal melting retains energy and consumes latent heat", () =>
        {
            var p = Make(Element.Copper, 1085);
            double mass = p.Mass;
            p.Energy += mass * Thermodynamics.FusionHeat(Element.Copper) * .5;
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Copper, "premature metal melting");
            Near(p.Temperature, 1085, 1e-8);
            p.Energy = mass * Thermodynamics.LiquidEnthalpy(Element.Copper, 1200);
            double energy = p.Energy;
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Lava && p.MeltOrigin == Element.Copper, "molten origin lost");
            Near(p.Temperature, 1200, 1e-8); Near(p.Energy, energy, 1e-12);
            p.Energy = mass * 385 * 1000;
            energy = p.Energy; Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Copper, "metal failed to solidify");
            Near(p.Temperature, 1000, 1e-8); Near(p.Energy, energy, 1e-12);
        });
        Test("salt dissolution conserves mass and energy", () =>
        {
            var w = new World(16, 16) { AmbientCooling = false };
            Scenes.Rect(w, 0, 0, 16, 16, Element.Glass);
            w.Set(8, 8, Element.Salt); w.Set(8, 9, Element.Water);
            var before = w.Statistics();
            for (int n = 0; n < 100; n++) w.Step();
            var after = w.Statistics();
            Near(before.Mass, after.Mass, 1e-12); Near(before.Energy, after.Energy, 1e-8);
            Check(w.Cells.Any(p => p.DissolvedSalt > 0), "salt did not dissolve");
        });
        Test("heated wood ignites and retains its mass", () =>
        {
            var w = new World(16, 16) { AmbientCooling = false };
            w.Set(8, 8, Element.Wood, 450);
            double mass = w.Statistics().Mass;
            w.Step();
            Check(w.Cells.Any(p => p.Element == Element.Fire), "no ignition");
            Near(w.Statistics().Mass, mass, 1e-15);
        });
        Test("gas leaving the top is recorded in the mass budget", () =>
        {
            var w = new World(16, 16) { AmbientCooling = false };
            w.Set(8, 0, Element.Steam); double mass = w.Statistics().Mass;
            w.Step(); Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-15);
            Check(w.EscapedMass > 0, "top did not vent");
        });
        Test("save/load round trip retains particle state", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-test-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(32, 24) { AmbientCooling = false };
                w.Set(3, 3, Element.Copper, 155); w.Set(5, 5, Element.Ice);
                w.At(3, 3).VelocityY = 12;
                WorldFile.Save(w, path);
                var restored = WorldFile.Load(path, 32, 24);
                Near(w.Statistics().Mass, restored.Statistics().Mass, 1e-15);
                Near(w.Statistics().Energy, restored.Statistics().Energy, 1e-12);
                Check(!restored.AmbientCooling && restored.At(3, 3).VelocityY == 12, "state lost");
                bool rejected = false;
                try { WorldFile.Load(path, 16, 16); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "invalid dimensions accepted");
                File.WriteAllText(path, "{\"Version\":1,\"Width\":32,\"Height\":24,\"Cooling\":true,\"Cells\":[null]}");
                rejected = false;
                try { WorldFile.Load(path, 32, 24); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "malformed particle accepted");
            }
            finally { File.Delete(path); }
        });
        Test("seeded runs are reproducible", () =>
        {
            World a = new(32, 32), b = new(32, 32);
            Scenes.Rect(a, 6, 3, 8, 8, Element.Sand);
            Scenes.Rect(b, 6, 3, 8, 8, Element.Sand);
            for (int n = 0; n < 60; n++) { a.Step(); b.Step(); }
            Check(a.Cells.SequenceEqual(b.Cells), "determinism lost");
        });
        Test("full demo remains finite and conserves the mass budget", () =>
        {
            var w = new World(SandboxApp.GridWidth, SandboxApp.GridHeight);
            Scenes.Demo(w); double mass = w.Statistics().Mass;
            var timer = Stopwatch.StartNew();
            for (int n = 0; n < 360; n++) w.Step();
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-9);
            Check(w.Cells.All(p => double.IsFinite(p.Energy) && double.IsFinite(p.Temperature)
                && p.Mass >= 0 && p.Temperature >= -273.151), "nonphysical numerical state");
            Console.WriteLine($"  demo: {w.Count:N0} particles, 360 steps in {timer.Elapsed.TotalSeconds:F2}s");
        });
        Console.WriteLine($"\n{passed} passed, {failed} failed. Headless physics and scene checks completed.");
        return failed == 0 ? 0 : 1;
    }

    private static World Tube()
    {
        var w = new World(16, 20) { AmbientCooling = false };
        Scenes.Rect(w, 0, 0, 16, 20, Element.Glass);
        for (int y = 1; y < 18; y++) w.Set(8, y, Element.Empty);
        return w;
    }

    private static Particle Make(Element e, double t)
    {
        var w = new World(8, 8); w.Set(1, 1, e, t); return w.At(1, 1);
    }
    private static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine($"PASS  {name}"); }
        catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL  {name}: {ex.Message}"); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, double tolerance)
        => Check(Math.Abs(actual - expected) <= tolerance, $"expected {expected:R}, got {actual:R}");
}
