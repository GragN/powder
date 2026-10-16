using System.Text.Json.Nodes;

namespace Powder;

public static partial class SelfTests
{
    private static void RunLiquidTests()
    {
        Test("liquid Fourier conduction agrees with thermal resistance and heat capacity", () =>
        {
            foreach (var e in new[] { Element.Water, Element.Oil, Element.Lava, Element.Copper, Element.Iron, Element.Glass })
            {
                double t = e == Element.Lava ? 1400 : Thermodynamics.CanMelt(e) ? Thermodynamics.MeltingPoint(e) + 100 : 20;
                var a = Make(e, t); var b = Make(e, t + 30);
                double ca = a.HeatCapacity, cb = b.HeatCapacity, total = a.Energy + b.Energy;
                double g = LiquidProperties.Conductivity(a) * World.Depth;
                double equilibrium = (ca * t + cb * (t + 30)) / (ca + cb);
                double decay = Math.Exp(-g * (1 / ca + 1 / cb) * .3);
                Thermodynamics.Exchange(ref a, ref b, .3);
                Near(a.Temperature, equilibrium + (t - equilibrium) * decay, 1e-9);
                Near(b.Temperature, equilibrium + (t + 30 - equilibrium) * decay, 1e-9);
                Near(a.Energy + b.Energy, total, 1e-10);
            }
        });
        Test("liquid conductivity gives the expected macroscopic thermal diffusivity", () =>
        {
            foreach (var e in new[] { Element.Water, Element.Oil })
            {
                const int count = 32;
                var row = Enumerable.Range(0, count).Select(i => Make(e, 40 + 10 * Math.Cos(Math.PI * (i + .5) / count))).ToArray();
                double total = row.Sum(p => p.Energy);
                const double dt = .5, duration = 300;
                for (int step = 0; step < duration / dt; step++)
                for (int j = 0; j < count - 1; j++)
                {
                    int i = (step & 1) == 0 ? j : count - 2 - j;
                    Thermodynamics.Exchange(ref row[i], ref row[i + 1], dt);
                }
                var m = Materials.Get(e);
                double alpha = m.Conductivity / (m.Density * m.SpecificHeat);
                double expected = 10 * Math.Exp(-alpha * Math.Pow(Math.PI / (count * World.CellSize), 2) * duration);
                double measured = 2.0 / count * row.Select((p, i) => (p.Temperature - 40) * Math.Cos(Math.PI * (i + .5) / count)).Sum();
                Near(measured, expected, .015); Near(row.Sum(p => p.Energy), total, 1e-9);
            }
        });
        Test("water density peaks near 4 C and viscosity decreases with temperature", () =>
        {
            var freezing = Make(Element.Water, 0); var four = Make(Element.Water, 4); var warm = Make(Element.Water, 80);
            Check(LiquidProperties.Density(four) > LiquidProperties.Density(freezing), "water density anomaly missing");
            Check(LiquidProperties.Density(four) > LiquidProperties.Density(warm), "thermal expansion missing");
            Near(LiquidProperties.DynamicViscosity(Make(Element.Water, 20)), .001, .00002);
            Check(LiquidProperties.DynamicViscosity(freezing) > 4 * LiquidProperties.DynamicViscosity(warm), "temperature-dependent viscosity missing");
        });
        Test("hot water rises, a return current forms, and heat moves faster than conduction", () =>
        {
            var w = LiquidTank(40, 48, Element.Water, 20);
            Scenes.Rect(w, 15, 32, 10, 8, Element.Water, 80);
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            double start = LiquidHeatCenter(w, 20), minimumY = 0, maximumY = 0;
            for (int n = 0; n < 120; n++)
            {
                w.Liquids.Step(w, World.FixedDelta);
                minimumY = Math.Min(minimumY, w.Liquids.V.Min()); maximumY = Math.Max(maximumY, w.Liquids.V.Max());
            }
            double end = LiquidHeatCenter(w, 20);
            Console.WriteLine($"  liquid plume: heat center {start:F2} -> {end:F2} cells, v [{minimumY:F4}, {maximumY:F4}] m/s, divergence {w.Liquids.DivergenceRms:E2}/s");
            Check(end < start - 6, "heat was not carried upward by the liquid");
            Check(minimumY < -.005 && maximumY > .002, "no rising and returning flow");
            Check(w.Liquids.DivergenceRms < .001, "liquid pressure projection did not converge");
            Check(w.Cells.Where(p => p.Element == Element.Water).All(p => p.Temperature >= 20 - 1e-7 && p.Temperature <= 80 + 1e-7 && p.Mass > 0), "advection created an extremum");
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-8);
        });
        Test("cold water sinks but water below 4 C rises in 4 C surroundings", () =>
        {
            foreach (bool anomaly in new[] { false, true })
            {
                double background = anomaly ? 4 : 60, patch = anomaly ? 0 : 20;
                var w = LiquidTank(24, 32, Element.Water, background);
                Scenes.Rect(w, 9, 12, 6, 6, Element.Water, patch);
                for (int n = 0; n < 20; n++) w.Liquids.Step(w, World.FixedDelta);
                double v = w.Liquids.VelocityAtFine(12, 15).Y;
                Check(anomaly ? v < -1e-7 : v > .001, $"wrong cold-water buoyancy: {v:R}");
            }
        });
        Test("the complete world couples liquid convection, Fourier conduction and finite air", () =>
        {
            var w = new World(48, 56);
            Scenes.Rect(w, 4, 4, 40, 48, Element.Glass, 20);
            Scenes.Rect(w, 5, 5, 38, 46, Element.Water, 20);
            Scenes.Rect(w, 19, 43, 10, 8, Element.Water, 80);
            double start = LiquidHeatCenter(w, 20), mass = w.Statistics().Mass, energy = Budget(w);
            for (int n = 0; n < 120; n++) w.Step();
            Check(LiquidHeatCenter(w, 20) < start - 5, "world did not carry heat upward");
            Check(w.Air.ExcessEnergy > 0, "liquid and vessel did not exchange heat with air");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12); Near(Budget(w), energy, 1e-8);
        });
        Test("stable stratification and uniform temperature do not spontaneously stir", () =>
        {
            foreach (bool stratified in new[] { false, true })
            {
                var w = LiquidTank(24, 32, Element.Water, 20);
                if (stratified) for (int y = 1; y < 31; y++) for (int x = 1; x < 23; x++)
                    w.Set(x, y, Element.Water, 80 - y * 1.5);
                for (int n = 0; n < 60; n++) w.Liquids.Step(w, World.FixedDelta);
                Check(w.Liquids.U.Concat(w.Liquids.V).All(v => Math.Abs(v) < 2e-6), $"pressure failed to balance stable buoyancy: {w.Liquids.U.Concat(w.Liquids.V).Max(v => Math.Abs(v)):E3}");
            }
        });
        Test("oil, rock, molten metals and silica all develop thermal circulation", () =>
        {
            foreach (var e in new[] { Element.Oil, Element.Stone, Element.Copper, Element.Iron, Element.Glass, Element.Sand })
            {
                double t = e == Element.Oil ? 30 : Thermodynamics.MeltingPoint(e) + 200;
                var w = LiquidTank(20, 26, e, t);
                Scenes.Rect(w, 7, 16, 6, 5, e, t + (e == Element.Oil ? 100 : 200));
                double energy = w.Statistics().Energy;
                for (int n = 0; n < 20; n++) w.Liquids.Step(w, World.FixedDelta);
                double v = w.Liquids.VelocityAtFine(10, 18).Y;
                Check(v < -1e-10, $"{e} did not rise: {v:R}");
                Check(w.Liquids.U.Concat(w.Liquids.V).All(double.IsFinite), $"unstable {e} flow");
                Near(w.Statistics().Energy, energy, 1e-8);
            }
        });
        Test("a one-cell wall blocks liquid flow and advected heat", () =>
        {
            var w = LiquidTank(32, 32, Element.Water, 20);
            Scenes.Rect(w, 16, 1, 1, 30, Element.Glass);
            Scenes.Rect(w, 5, 20, 6, 6, Element.Water, 80);
            double energy = w.Statistics().Energy;
            for (int n = 0; n < 90; n++) w.Liquids.Step(w, World.FixedDelta);
            for (int y = 1; y < 31; y++)
            {
                Near(w.Liquids.U[y * 33 + 16], 0, 1e-12); Near(w.Liquids.U[y * 33 + 17], 0, 1e-12);
                for (int x = 17; x < 31; x++) Near(w.At(x, y).Temperature, 20, 1e-7);
            }
            Near(w.Statistics().Energy, energy, 1e-8);
        });
        Test("viscosity dissipates circulation more strongly in oil than in water", () =>
        {
            double Decay(Element e)
            {
                var w = LiquidTank(24, 24, e, 20);
                var state = w.Liquids.Capture();
                double Psi(int x, int y) => .0001 * Math.Sin(Math.PI * (x - 1) / 22) * Math.Sin(Math.PI * (y - 1) / 22);
                for (int y = 1; y < 23; y++) for (int x = 2; x < 23; x++)
                    state.U[y * 25 + x] = (Psi(x, y + 1) - Psi(x, y)) / World.CellSize;
                for (int y = 2; y < 23; y++) for (int x = 1; x < 23; x++)
                    state.V[y * 24 + x] = -(Psi(x + 1, y) - Psi(x, y)) / World.CellSize;
                w.Liquids.Restore(w, state);
                for (int n = 0; n < 60; n++) w.Liquids.Step(w, World.FixedDelta);
                return w.Liquids.U.Sum(v => v * v) + w.Liquids.V.Sum(v => v * v);
            }
            Check(Decay(Element.Oil) < Decay(Element.Water) * .2, "viscosity did not affect circulation");
        });
        Test("advected dissolved salt retains its mass and thermal energy", () =>
        {
            var w = LiquidTank(24, 32, Element.Water, 40);
            for (int y = 4; y < 10; y++) for (int x = 8; x < 15; x++)
            {
                ref var p = ref w.At(x, y);
                double salt = p.Mass * .05;
                p.Mass += salt; p.DissolvedSalt = salt; p.Energy += salt * Materials.Get(Element.Salt).SpecificHeat * 40;
            }
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy, saltMass = w.Cells.Sum(p => p.DissolvedSalt);
            for (int n = 0; n < 120; n++) w.Liquids.Step(w, World.FixedDelta);
            Near(w.Cells.Sum(p => p.DissolvedSalt), saltMass, 1e-14);
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-8);
            Check(w.Cells.Where((p, i) => i / w.Width > 15).Any(p => p.DissolvedSalt > 1e-9), "salt was not carried downward");
            Check(w.Cells.Where(p => p.Element == Element.Water).All(p => p.DissolvedSalt >= 0 && p.DissolvedSalt <= p.Mass), "invalid concentration");
        });
        Test("melts retain origin-specific heat capacity and conductivity", () =>
        {
            var copper = Make(Element.Copper, 1200); var iron = Make(Element.Iron, 1650); var rock = Make(Element.Lava, 1450);
            Near(copper.HeatCapacity / copper.Mass, 520, 1e-9); Near(iron.HeatCapacity / iron.Mass, 820, 1e-9);
            double initial = copper.Temperature;
            copper.Energy += copper.Mass * 520 * 10;
            Near(copper.Temperature, initial + 10, 1e-9);
            Check(LiquidProperties.Conductivity(copper) > LiquidProperties.Conductivity(rock) * 50, "all melts still share conductivity");
            Check(LiquidProperties.DynamicViscosity(Make(Element.Glass, 1800)) > LiquidProperties.DynamicViscosity(rock) * 1000, "silica viscosity missing");
        });
        Test("saves restore liquid flow, reject invalid velocities and migrate older melts", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-liquid-{Guid.NewGuid()}.json");
            try
            {
                var w = LiquidTank(20, 26, Element.Water, 20);
                Scenes.Rect(w, 7, 16, 6, 5, Element.Water, 80);
                for (int n = 0; n < 20; n++) w.Step();
                WorldFile.Save(w, path);
                var loaded = WorldFile.Load(path, 20, 26);
                Check(w.Liquids.U.SequenceEqual(loaded.Liquids.U) && w.Liquids.V.SequenceEqual(loaded.Liquids.V), "liquid velocity lost");
                double energy = loaded.Statistics().Energy;
                loaded.Step(); Near(loaded.Statistics().Energy, energy, 1e-8);
                var data = JsonNode.Parse(File.ReadAllText(path))!;
                data["Liquids"]!["U"]![0] = 200;
                File.WriteAllText(path, data.ToJsonString());
                bool rejected = false;
                try { WorldFile.Load(path, 20, 26); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "invalid liquid velocity accepted");
                w.Clear(); w.Set(5, 5, Element.Copper, 1200);
                WorldFile.Save(w, path); data = JsonNode.Parse(File.ReadAllText(path))!;
                data["Version"] = 2; data.AsObject().Remove("Liquids");
                double oldEnthalpy = Materials.Get(Element.Copper).SpecificHeat * 1085 + Thermodynamics.FusionHeat(Element.Copper) + 1200 * (1200 - 1085);
                data["Cells"]![0]!["Energy"] = w.At(5, 5).Mass * oldEnthalpy;
                File.WriteAllText(path, data.ToJsonString());
                loaded = WorldFile.Load(path, 20, 26);
                Near(loaded.At(5, 5).Temperature, 1200, 1e-9);
                Check(loaded.Liquids.U.All(v => v == 0), "old scene did not start with still liquid");
            }
            finally { File.Delete(path); }
        });
    }

    private static World LiquidTank(int width, int height, Element e, double temperature)
    {
        var w = new World(width, height) { AtmosphereEnabled = false };
        Scenes.Rect(w, 0, 0, width, height, Element.Glass, 20);
        Scenes.Rect(w, 1, 1, width - 2, height - 2, e, temperature);
        return w;
    }

    private static double LiquidHeatCenter(World w, double background)
    {
        double weighted = 0, sum = 0;
        for (int i = 0; i < w.Cells.Length; i++)
        {
            ref readonly var p = ref w.Cells[i];
            if (LiquidProperties.Kind(p) == 0) continue;
            double heat = p.Energy - Thermodynamics.EnergyAtTemperature(p, background);
            weighted += heat * (i / w.Width + .5); sum += heat;
        }
        return weighted / sum;
    }
}
