using System.Diagnostics;

namespace Powder;

public static partial class SelfTests
{
    private static int passed, failed;
    private static string? nameFilter;

    public static int Run(string? filter = null)
    {
        nameFilter = filter; passed = failed = 0;
        if (filter is not null) Console.WriteLine($"Test filter: {filter}");
        Test("material data uses SI units", () =>
        {
            Check(Materials.Get(Element.Water).SpecificHeat == 4184, "water heat capacity");
            Check(Materials.Get(Element.Copper).Conductivity > Materials.Get(Element.Wood).Conductivity * 1000,
                "copper / wood conductivity contrast");
        });
        Test("gravity moves sand and conserves mass", () =>
        {
            var w = new World(32, 32) { AtmosphereEnabled = false };
            w.Set(10, 2, Element.Sand);
            double mass = w.Statistics().Mass;
            for (int n = 0; n < 90; n++) w.Step();
            Check(w.Cells.Select((p, i) => (p, i)).Single(p => p.p.Element == Element.Sand).i / w.Width > 25,
                "sand did not fall");
            Near(w.Statistics().Mass, mass, 1e-15);
        });
        Test("free fall follows v=gt and y=gt²/2 beyond the old speed limit", () =>
        {
            var w = new World(16, 1024) { AtmosphereEnabled = false };
            w.Set(8, 2, Element.Sand);
            for (int n = 1; n <= 20; n++)
            {
                w.Step();
                int index = Array.FindIndex(w.Cells, p => p.Element == Element.Sand);
                ref var p = ref w.Cells[index];
                double time = n * World.FixedDelta;
                Near(p.VelocityY * World.CellSize, World.Gravity * time, 1e-10);
                Near((index / w.Width - 2 + p.Travel) * World.CellSize,
                    .5 * World.Gravity * time * time, 1e-10);
                if (n == 1) Check(index / w.Width == 2, "subcell travel moved an entire cell too early");
            }
            Check(w.Cells.Any(p => p.VelocityY > 240), "old terminal-speed cap is still present");
        });
        Test("different masses have the same free-fall acceleration", () =>
        {
            var w = new World(32, 512) { AtmosphereEnabled = false };
            w.Set(8, 2, Element.Sand); w.Set(24, 2, Element.Salt);
            for (int n = 0; n < 15; n++) w.Step();
            int a = Array.FindIndex(w.Cells, p => p.Element == Element.Sand);
            int b = Array.FindIndex(w.Cells, p => p.Element == Element.Salt);
            Check(a / w.Width == b / w.Width, "mass changed the acceleration in free space");
            Near(w.Cells[a].VelocityY, w.Cells[b].VelocityY, 1e-10);
            Near(w.Cells[a].Travel, w.Cells[b].Travel, 1e-10);
        });
        Test("fast particles cannot tunnel through a one-cell floor", () =>
        {
            var w = new World(32, 96) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 20, 32, 1, Element.Stone);
            w.Set(8, 2, Element.Sand); w.At(8, 2).VelocityY = 10_000;
            double mass = w.Statistics().Mass;
            w.Step();
            Check(w.At(8, 19).Element == Element.Sand, "particle passed through its first obstacle");
            Near(w.At(8, 19).VelocityY, 0, 1e-12); Near(w.At(8, 19).Travel, 0, 1e-12);
            Near(w.Statistics().Mass, mass, 1e-12);
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
            var w = new World(48, 24) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 22, 48, 2, Element.Stone);
            Scenes.Rect(w, 0, 5, 1, 17, Element.Stone);
            Scenes.Rect(w, 47, 5, 1, 17, Element.Stone);
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
        Test("heater supplies P times dt joules and respects m times c", () =>
        {
            var w = new World(24, 16);
            w.Set(4, 4, Element.Copper); w.Set(12, 4, Element.Water);
            ref var copper = ref w.At(4, 4); ref var water = ref w.At(12, 4);
            water.Mass = copper.Mass;
            water.Energy = water.Mass * Thermodynamics.SpecificEnthalpy(Element.Water, 20);
            double a = copper.Energy, b = water.Energy, q = 20 * .05;
            w.HeatBrush(4, 4, 0, 20, .05); w.HeatBrush(12, 4, 0, 20, .05);
            Near(copper.Energy - a, q, 1e-12); Near(water.Energy - b, q, 1e-12);
            Near(copper.Temperature - 20, q / (copper.Mass * 385), 1e-10);
            Near(water.Temperature - 20, q / (water.Mass * 4184), 1e-10);
            Check(copper.Temperature - 20 > 10 * (water.Temperature - 20), "heater still adds identical degrees");
            Near(w.ExternalEnergy, 2 * q, 1e-12);
        });
        Test("doubling mass halves heating and heater energy is frame independent", () =>
        {
            var a = new World(16, 16); var b = new World(16, 16);
            a.Set(8, 8, Element.Copper); b.Set(8, 8, Element.Copper);
            b.At(8, 8).Mass *= 2; b.At(8, 8).Energy *= 2;
            for (int n = 0; n < 30; n++) a.HeatBrush(8, 8, 0, 10, .5 / 30);
            for (int n = 0; n < 120; n++) b.HeatBrush(8, 8, 0, 10, .5 / 120);
            Near(a.ExternalEnergy, 5, 1e-10); Near(b.ExternalEnergy, 5, 1e-10);
            Near(a.At(8, 8).Temperature - 20, 2 * (b.At(8, 8).Temperature - 20), 1e-9);
        });
        Test("conductive transfer matches the two-body thermal resistance solution", () =>
        {
            Particle a = Make(Element.Copper, 20), b = Make(Element.Iron, 120);
            double ca = a.HeatCapacity, cb = b.HeatCapacity;
            double g = 1 / (.5 * World.CellSize / (401 * World.CellSize * World.Depth)
                + .5 * World.CellSize / (80 * World.CellSize * World.Depth));
            double equilibrium = (ca * 20 + cb * 120) / (ca + cb);
            double decay = Math.Exp(-g * (1 / ca + 1 / cb) * .01);
            Thermodynamics.Exchange(ref a, ref b, .01);
            Near(a.Temperature, equilibrium + (20 - equilibrium) * decay, 1e-10);
            Near(b.Temperature, equilibrium + (120 - equilibrium) * decay, 1e-10);
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
            Check(p.Element == Element.Steam, "steam condensed before releasing latent heat");
            Near(p.Temperature, 100, 1e-10);
            p.Energy = mass * Thermodynamics.BoilingLiquid;
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Water, "no condensation after latent heat release"); Near(p.Mass, mass, 1e-15);
        });
        Test("water releases the complete latent heat before freezing", () =>
        {
            var p = Make(Element.Water, 0);
            p.Energy *= .5;
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Water, "water froze before releasing latent heat");
            Near(p.Temperature, 0, 1e-12);
            p.Energy = 0; Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Ice, "water did not freeze at the end of the plateau");
            p.Energy = p.Mass * 2100 * -10;
            Near(p.Temperature, -10, 1e-10);
        });
        Test("conduction crosses boiling without losing energy or overshooting equilibrium", () =>
        {
            Particle cold = Make(Element.Water, 90), hot = Make(Element.Copper, 1000);
            double energy = cold.Energy + hot.Energy, mass = cold.Mass;
            double equilibrium = (energy - mass * (Thermodynamics.BoilingGas - 1996 * 100))
                / (hot.Mass * 385 + mass * 1996);
            Thermodynamics.Exchange(ref cold, ref hot, 1_000_000);
            Check(cold.Element == Element.Steam, "water did not pay for full vaporization");
            Near(cold.Energy + hot.Energy, energy, 1e-9);
            Near(cold.Temperature, equilibrium, 1e-7); Near(hot.Temperature, equilibrium, 1e-7);
        });
        Test("two latent plateaus conduct finite heat at constant temperatures", () =>
        {
            Particle cold = Make(Element.Ice, 0), hot = Make(Element.Water, 100);
            cold.Energy = cold.Mass * Thermodynamics.Fusion * .5;
            hot.Energy += hot.Mass * Thermodynamics.Vaporization * .5;
            double energy = cold.Energy + hot.Energy, before = cold.Energy;
            double expected = 2 * 2.22 * .6 / (2.22 + .6) * World.Depth * 100 * .01;
            Thermodynamics.Exchange(ref cold, ref hot, .01);
            Near(cold.Energy - before, expected, 1e-12);
            Near(cold.Temperature, 0, 1e-12); Near(hot.Temperature, 100, 1e-12);
            Near(cold.Energy + hot.Energy, energy, 1e-10);
        });
        Test("external-air boundary exchange also pays latent heat", () =>
        {
            var p = Make(Element.Ice, 0);
            p.Energy = p.Mass * Thermodynamics.Fusion * .5;
            double before = p.Energy;
            double q = Thermodynamics.ExchangeWithReservoir(ref p, 20, .01, 1);
            Near(q, .2, 1e-12); Near(p.Energy - before, q, 1e-12);
            Near(p.Temperature, 0, 1e-12);
            Check(p.Element == Element.Ice, "ambient warmth bypassed latent heat");
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
        Test("molten sand solidifies as glass with continuous temperature and conserved energy", () =>
        {
            var p = Make(Element.Sand, 1800);
            double mass = p.Mass;
            Check(p.Element == Element.Lava && p.MeltOrigin == Element.Sand, "sand did not become silica melt");
            Near(p.Temperature, 1800, 1e-8);
            p.Energy = mass * 830 * 1700;
            double energy = p.Energy;
            Thermodynamics.ResolvePhase(ref p);
            Check(p.Element == Element.Glass, "sand returned to sand instead of glass");
            Near(p.Temperature, 1700, 1e-8); Near(p.Mass, mass, 1e-15); Near(p.Energy, energy, 1e-12);
            Check(p.VelocityY == 0 && p.Travel == 0, "solid glass kept a falling velocity");
            double offset = p.EnthalpyOffset;
            for (int n = 0; n < 3; n++)
            {
                p.Energy = Thermodynamics.EnergyAtTemperature(p, 1800);
                Thermodynamics.ResolvePhase(ref p);
                Check(p.Element == Element.Lava && p.MeltOrigin == Element.Glass, "glass remelting lost composition");
                p.Energy = mass * (offset + 840 * 1600);
                energy = p.Energy; Thermodynamics.ResolvePhase(ref p);
                Check(p.Element == Element.Glass, "cooled glass changed composition");
                Near(p.Temperature, 1600, 1e-8); Near(p.Energy, energy, 1e-12);
                Near(p.EnthalpyOffset, offset, 1e-12);
            }
        });
        Test("heating and cooling tools complete the sand-to-glass cycle", () =>
        {
            var w = new World(16, 16);
            w.Set(8, 8, Element.Sand);
            ref var p = ref w.At(8, 8);
            double mass = p.Mass, before = p.Energy;
            double hot = mass * Thermodynamics.LiquidEnthalpy(Element.Sand, 1800);
            w.HeatBrush(8, 8, 0, hot - before, 1);
            Check(p.Element == Element.Lava, "heater did not melt sand");
            double cold = mass * 830 * 1700;
            w.HeatBrush(8, 8, 0, cold - p.Energy - .001, 1);
            Check(p.Element == Element.Glass, "cooling tool did not make glass");
            Near(p.Energy - before, w.ExternalEnergy, 1e-10); Near(p.Mass, mass, 1e-15);
        });
        Test("external-air boundary cooling carries molten sand through solidification to glass", () =>
        {
            var p = Make(Element.Sand, 1800);
            double mass = p.Mass, before = p.Energy;
            double q = Thermodynamics.ExchangeWithReservoir(ref p, 20, 1, 1000);
            Check(p.Element == Element.Glass, "silica melt did not vitrify in the heat solver");
            Near(p.Temperature, 20, 1e-8); Near(p.Mass, mass, 1e-15);
            Near(p.Energy - before, q, 1e-12);
        });
        Test("closed two-dimensional heat flow conserves energy across phase changes", () =>
        {
            var w = new World(32, 32) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 0, 32, 32, Element.Glass);
            Scenes.Rect(w, 7, 8, 8, 8, Element.Copper, 500);
            Scenes.Rect(w, 15, 8, 8, 8, Element.Ice, -20);
            var before = w.Statistics();
            for (int n = 0; n < 300; n++) w.Step();
            var after = w.Statistics();
            Near(after.Mass, before.Mass, 1e-10); Near(after.Energy, before.Energy, 1e-8);
            Check(w.Cells.All(p => p.Temperature >= -20.000001 && p.Temperature <= 500.000001),
                "conduction created an artificial hot or cold extremum");
            Check(w.Cells.Any(p => p.Element == Element.Water || p.Element == Element.Steam),
                "thermal field never melted any ice");
        });
        Test("salt dissolution conserves mass and energy", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
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
            var w = new World(16, 16) { AtmosphereEnabled = false };
            w.Set(8, 8, Element.Wood, 450);
            double mass = w.Statistics().Mass;
            w.Step();
            Check(w.Cells.Any(p => p.Element == Element.Fire), "no ignition");
            Near(w.Statistics().Mass, mass, 1e-15);
        });
        Test("gas leaving the top is recorded in the mass budget", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
            w.Set(8, 0, Element.Steam); w.At(8, 0).VelocityY = -120; double mass = w.Statistics().Mass;
            w.Step(); Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-15);
            Check(w.EscapedMass > 0, "top did not vent");
        });
        Test("save/load round trip retains particle state", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-test-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(32, 24) { AtmosphereEnabled = false };
                w.Set(3, 3, Element.Copper, 155); w.Set(5, 5, Element.Ice);
                w.Set(10, 10, Element.Sand, 1800);
                w.At(10, 10).Energy = w.At(10, 10).Mass * 830 * 1700;
                Thermodynamics.ResolvePhase(ref w.At(10, 10));
                w.At(3, 3).VelocityY = 12;
                WorldFile.Save(w, path);
                var restored = WorldFile.Load(path, 32, 24);
                Near(w.Statistics().Mass, restored.Statistics().Mass, 1e-15);
                Near(w.Statistics().Energy, restored.Statistics().Energy, 1e-12);
                Check(!restored.AtmosphereEnabled && restored.At(3, 3).VelocityY == 12, "state lost");
                Check(restored.At(10, 10).Element == Element.Glass, "glass composition lost");
                Near(restored.At(10, 10).Temperature, w.At(10, 10).Temperature, 1e-10);
                Near(restored.At(10, 10).EnthalpyOffset, w.At(10, 10).EnthalpyOffset, 1e-12);
                bool rejected = false;
                try { WorldFile.Load(path, 16, 16); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "invalid dimensions accepted");
                var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                foreach (var cell in legacy["Cells"]!.AsArray()) cell!.AsObject().Remove("EnthalpyOffset");
                File.WriteAllText(path, legacy.ToJsonString());
                var oldScene = WorldFile.Load(path, 32, 24);
                Near(oldScene.At(3, 3).Temperature, 155, 1e-10);
                File.WriteAllText(path, "{\"Version\":1,\"Width\":32,\"Height\":24,\"Cooling\":true,\"Cells\":[null]}");
                rejected = false;
                try { WorldFile.Load(path, 32, 24); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "malformed particle accepted");
            }
            finally { File.Delete(path); }
        });
        Test("saved free fall retains fractional position and acceleration", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-fall-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(16, 512) { AtmosphereEnabled = false };
                w.Set(8, 2, Element.Sand);
                for (int n = 0; n < 8; n++) w.Step();
                WorldFile.Save(w, path);
                var loaded = WorldFile.Load(path, 16, 512);
                for (int n = 0; n < 8; n++) { w.Step(); loaded.Step(); }
                int a = Array.FindIndex(w.Cells, p => p.Element == Element.Sand);
                int b = Array.FindIndex(loaded.Cells, p => p.Element == Element.Sand);
                Check(a == b, "restored particle changed trajectory");
                Near(w.Cells[a].VelocityY, loaded.Cells[b].VelocityY, 1e-12);
                Near(w.Cells[a].Travel, loaded.Cells[b].Travel, 1e-12);
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
        RunAtmosphereTests();
        RunAirPocketTests();
        RunFiniteVolumeAirTests();
        RunLiquidTests();
        RunLiquidSolverTests();
        RunVaporizationTests();
        RunBoilingBehaviorTests();
        RunRigidBodyTests();
        Test("full demo remains finite and conserves the mass budget", () =>
        {
            var w = new World(SandboxApp.GridWidth, SandboxApp.GridHeight);
            Scenes.Demo(w); double mass = w.Statistics().Mass;
            double energy = Budget(w);
            var timer = Stopwatch.StartNew();
            for (int n = 0; n < 360; n++) w.Step();
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-9);
            Near(Budget(w), energy, 1e-6);
            Check(w.Air.Heat.All(double.IsFinite) && w.Air.U.All(double.IsFinite) && w.Air.V.All(double.IsFinite),
                "nonfinite atmospheric state");
            Check(w.Liquids.DivergenceRms < .01, $"liquid projection divergence {w.Liquids.DivergenceRms}");
            Check(w.Air.DivergenceRms < .01, $"full-scene air pressure projection did not converge: {w.Air.DivergenceRms}");
            Check(w.Cells.All(p => double.IsFinite(p.Energy) && double.IsFinite(p.Temperature)
                && p.Mass >= 0 && p.Temperature >= -273.151), "nonphysical numerical state");
            Console.WriteLine($"  demo: {w.Count:N0} particles, 360 steps in {timer.Elapsed.TotalSeconds:F2}s; air divergence RMS {w.Air.DivergenceRms:F5}/s; liquid {w.Liquids.DivergenceRms:F5}/s");
        });
        Console.WriteLine($"\n{passed} passed, {failed} failed. Headless physics and scene checks completed.");
        return failed == 0 && passed > 0 ? 0 : 1;
    }

    private static World Tube()
    {
        var w = new World(16, 20) { AtmosphereEnabled = false };
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
        if (nameFilter is not null && !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) return;
        try { run(); passed++; Console.WriteLine($"PASS  {name}"); }
        catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL  {name}: {ex.Message}"); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, double tolerance)
        => Check(Math.Abs(actual - expected) <= tolerance, $"expected {expected:R}, got {actual:R}");
}
