using System.Text.Json;
using System.Text.Json.Nodes;

namespace Powder;

public static partial class SelfTests
{
    private static void RunSurfaceEvaporationTests()
    {
        Test("surface evaporation occurs below boiling and increases with water temperature", () =>
        {
            double previousMass = 0, previousEnergy = 0;
            foreach (double temperature in new[] { 20.0, 60.0, 90.0 })
            {
                var w = EvaporationCup(temperature);
                double mass = w.Statistics().Mass, budget = Budget(w);
                double waterMass = w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass);
                w.Step();
                var vapors = w.Cells.Concat(w.Bubbles).Where(p => p.Element == Element.Steam).ToArray();
                double vaporMass = vapors.Sum(p => p.Mass), vaporEnergy = vapors.Sum(p => p.Energy);
                Check(vaporMass > previousMass, $"water at {temperature} C did not evaporate faster than colder water");
                Check(vaporEnergy > previousEnergy, "colder water did not lose less energy per simulation step");
                Check(vaporMass < waterMass * .01, "sub-boiling evaporation consumed an entire water cell");
                Check(w.Cells.Any(p => p.Element == Element.Water && p.Temperature < temperature),
                    "evaporation did not cool the remaining water");
                Near(w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass) + vaporMass,
                    waterMass, 1e-14);
                foreach (var vapor in vapors)
                {
                    Near(vapor.Temperature, temperature, 1e-7);
                    Near(vapor.CondensationOffset, 100 - temperature, 1e-10);
                    Near(vapor.Energy / vapor.Mass, Thermodynamics.WaterVaporEnthalpy(temperature), 1e-7);
                }
                Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
                Near(Budget(w), budget, 1e-9);
                previousMass = vaporMass; previousEnergy = vaporEnergy;
            }
        });
        Test("surface evaporation retains salt and conserves mass and energy", () =>
        {
            var w = EvaporationCup(90);
            ref var water = ref w.At(8, 10);
            water.DissolvedSalt = water.Mass * .05;
            water.Energy = Thermodynamics.EnergyAtTemperature(water, 90);
            double salt = water.DissolvedSalt, sourceMass = water.Mass;
            double mass = w.Statistics().Mass, budget = Budget(w);
            w.Step();
            var all = w.Cells.Concat(w.Bubbles).ToArray();
            double vaporMass = all.Where(p => p.Element == Element.Steam).Sum(p => p.Mass);
            Check(vaporMass > 0, "warm salt water did not evaporate");
            Check(all.Where(p => p.Element == Element.Steam).All(p => p.DissolvedSalt == 0),
                "surface evaporation carried nonvolatile salt into vapor");
            Near(all.Sum(p => p.DissolvedSalt), salt, 1e-15);
            Near(all.Where(p => Materials.IsWater(p.Element)).Sum(p => p.Mass), sourceMass, 1e-14);
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            Near(Budget(w), budget, 1e-9);
        });
        Test("surface evaporation requires a water surface exposed to gas", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 0, w.Width, w.Height, Element.Glass, 90);
            Scenes.Rect(w, 7, 7, 3, 3, Element.Water, 90);
            double waterMass = w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass);
            double budget = Budget(w);
            for (int step = 0; step < 12; step++) w.Step();
            Check(w.Cells.Concat(w.Bubbles).All(p => p.Element != Element.Steam),
                "sub-boiling vapor formed inside water or crossed a solid enclosure");
            Near(w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass), waterMass, 1e-13);
            Near(w.At(8, 8).Temperature, 90, 1e-9);
            Near(Budget(w), budget, 1e-8);
        });
        Test("evaporation near freezing preserves the thermal budget and never creates negative mass", () =>
        {
            foreach (double temperature in new[] { 0.0, .01 })
            {
                var w = EvaporationCup(temperature);
                double mass = w.Statistics().Mass, budget = Budget(w);
                for (int step = 0; step < 120; step++) w.Step();
                Check(VaporMass(w) + w.EscapedMass > 0, "liquid at its freezing point did not evaporate");
                Check(w.Cells.Concat(w.Bubbles).All(p => p.Mass >= 0 && double.IsFinite(p.Energy)
                    && double.IsFinite(p.Temperature) && p.Temperature >= -273.15), "evaporation created a nonphysical particle");
                Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
                Near(Budget(w), budget, 1e-8);
            }
        });
        Test("surface evaporation and condensation conserve energy with the finite atmosphere", () =>
        {
            var w = EvaporationCup(90);
            w.AtmosphereEnabled = true;
            double mass = w.Statistics().Mass, budget = Budget(w);
            for (int step = 0; step < 180; step++) w.Step();
            Check(w.Air.ExcessEnergy > 0, "hot water and its vapor did not heat the atmosphere");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            Near(Budget(w), budget, 1e-8);
        });
        Test("tiny condensate shares air volume instead of becoming a full-cell obstacle", () =>
        {
            foreach (var element in new[] { Element.Water, Element.Ice })
            {
                var w = new World(32, 32);
                w.Set(16, 8, element, element == Element.Ice ? -20 : 20);
                w.At(16, 8).Mass *= .001; w.At(16, 8).Energy *= .001;
                double mass = w.Statistics().Mass, budget = Budget(w);
                for (int step = 0; step < 12; step++) w.Step();
                Check(w.BubbleLocations.Length > 0, "tiny water drop was discarded instead of dispersed");
                Check(w.Cells.All(p => !World.SubcellDrop(p)), "a subcell drop created a full pressure cell");
                Near(w.Air.TotalAirVolume, w.Width * w.Height * World.CellVolume, 1e-14);
                Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
                Near(Budget(w), budget, 1e-8);
            }
        });
        Test("surface vapor releases its full latent heat before becoming a small water droplet", () =>
        {
            const double temperature = 90;
            double mass = Materials.Get(Element.Water).Density * World.CellVolume * .001;
            var vapor = SurfaceVapor(temperature, mass);
            Thermodynamics.ResolvePhase(ref vapor);
            Check(vapor.Element == Element.Steam, "fresh sub-boiling vapor immediately became liquid");
            Near(vapor.Temperature, temperature, 1e-9);
            double liquidH = Thermodynamics.SpecificEnthalpy(Element.Water, temperature);
            double latent = Thermodynamics.WaterVaporEnthalpy(temperature) - liquidH;
            Check(latent > Thermodynamics.Vaporization,
                "evaporation below boiling incorrectly discounted the latent heat per kilogram");
            double energy = vapor.Energy;
            const double conductance = .05, reservoir = 20;
            double halfLatentTime = mass * latent * .5 / (conductance * (temperature - reservoir));
            double q = Thermodynamics.ExchangeWithReservoir(ref vapor, reservoir, conductance, halfLatentTime);
            Near(q, -mass * latent * .5, 1e-10);
            Near(vapor.Energy - energy, q, 1e-12);
            Check(vapor.Element == Element.Steam, "partially cooled vapor skipped its condensation plateau");
            Near(vapor.Temperature, temperature, 1e-8);
            Near(vapor.Mass, mass, 1e-16);
            energy = vapor.Energy;
            q = Thermodynamics.ExchangeWithReservoir(ref vapor, reservoir, conductance, 10);
            Check(vapor.Element == Element.Water, "cooled sub-boiling vapor did not become water");
            Near(vapor.Mass, mass, 1e-16);
            Near(vapor.Energy - energy, q, 1e-12);
            Near(vapor.Temperature, reservoir, 1e-7);
            Near(vapor.CondensationOffset, 0, 0);
            vapor.Energy = Thermodynamics.EnergyAtTemperature(vapor, 95);
            Thermodynamics.ResolvePhase(ref vapor);
            Check(vapor.Element == Element.Water, "condensed water inherited the old vapor condensation point as a boiling point");
            Near(vapor.Temperature, 95, 1e-8);
        });
        Test("two falling half-mass water droplets merge into one full cell", () =>
        {
            var w = SealedDropletPair(false);
            w.At(8, 7).VelocityY = 60;
            double mass = w.Statistics().Mass, budget = Budget(w);
            double waterMass = w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass);
            w.Step();
            var droplets = w.Cells.Where(p => p.Element == Element.Water).ToArray();
            Check(droplets.Length == 1, "two vertically colliding half droplets remained separate");
            Near(droplets[0].Mass, waterMass, 1e-14);
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            Near(Budget(w), budget, 1e-8);
        });
        Test("horizontal half-droplet collisions merge mass energy and momentum", () =>
        {
            var w = SealedDropletPair(true);
            w.At(7, 8).VelocityX = 60;
            double momentum = w.Cells.Sum(p => p.Mass * p.VelocityX);
            double mass = w.Statistics().Mass, budget = Budget(w);
            double waterMass = w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass);
            w.Step();
            var droplets = w.Cells.Where(p => p.Element == Element.Water).ToArray();
            Check(droplets.Length == 1, "two horizontally colliding half droplets remained separate");
            Near(droplets[0].Mass, waterMass, 1e-14);
            Near(w.Cells.Sum(p => p.Mass * p.VelocityX), momentum, 1e-12);
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            Near(Budget(w), budget, 1e-8);
        });
        Test("surface vapor snapshots retain condensation state and migrate older scenes", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-surface-vapor-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(16, 16) { AtmosphereEnabled = false };
                w.At(8, 8) = SurfaceVapor(90, 1e-8);
                w.Bubbles[10 * w.Width + 10] = SurfaceVapor(35, 2e-9);
                w.RebuildBubbleIndices();
                WorldFile.Save(w, path);
                var document = JsonNode.Parse(File.ReadAllText(path))!;
                Check(document["Version"]!.GetValue<int>() == 8, "surface vapor did not introduce the version-eight save format");
                var restored = WorldFile.Load(path, w.Width, w.Height);
                foreach (var pair in new[] { (w.At(8, 8), restored.At(8, 8)),
                    (w.Bubbles[10 * w.Width + 10], restored.Bubbles[10 * w.Width + 10]) })
                {
                    Check(pair.Item2.Element == Element.Steam, "saved surface vapor changed phase");
                    Near(pair.Item2.Mass, pair.Item1.Mass, 1e-18);
                    Near(pair.Item2.Energy, pair.Item1.Energy, 1e-14);
                    Near(pair.Item2.Temperature, pair.Item1.Temperature, 1e-9);
                    Near(pair.Item2.CondensationOffset, pair.Item1.CondensationOffset, 0);
                }
                document["Version"] = 7;
                foreach (string layer in new[] { "Cells", "Bubbles" })
                    foreach (var cell in document[layer]!.AsArray()) cell!.AsObject().Remove("CondensationOffset");
                File.WriteAllText(path, document.ToJsonString());
                restored = WorldFile.Load(path, w.Width, w.Height);
                Near(restored.At(8, 8).CondensationOffset, 0, 0);
                Near(restored.Bubbles[10 * w.Width + 10].CondensationOffset, 0, 0);
                Near(restored.At(8, 8).Temperature, 100, 1e-9);
                Near(restored.Statistics().Mass, w.Statistics().Mass, 1e-17);
                Near(restored.Statistics().Energy, w.Statistics().Energy, 1e-12);
            }
            finally { File.Delete(path); }
        });
        Test("surface vapor snapshots reject invalid condensation temperatures", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-invalid-surface-vapor-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(16, 16) { AtmosphereEnabled = false };
                w.At(8, 8) = SurfaceVapor(90, 1e-8);
                WorldFile.Save(w, path);
                string valid = File.ReadAllText(path);
                foreach (double invalid in new[] { -1.0, 101.0 })
                {
                    var document = JsonNode.Parse(valid)!;
                    document["Cells"]![0]!["CondensationOffset"] = invalid;
                    File.WriteAllText(path, document.ToJsonString());
                    Check(RejectsInvalidSurfaceSnapshot(path), "an out-of-range condensation temperature was accepted");
                }
                var liquidDocument = JsonNode.Parse(valid)!;
                liquidDocument["Cells"]![0]!["Element"] = (int)Element.Water;
                File.WriteAllText(path, liquidDocument.ToJsonString());
                Check(RejectsInvalidSurfaceSnapshot(path), "a liquid cell retained a vapor-only condensation temperature");
                var nonfiniteDocument = JsonNode.Parse(valid)!;
                nonfiniteDocument["Cells"]![0]!["CondensationOffset"] = "NONFINITE_OFFSET";
                File.WriteAllText(path, nonfiniteDocument.ToJsonString().Replace("\"NONFINITE_OFFSET\"", "1e999"));
                Check(RejectsInvalidSurfaceSnapshot(path), "a nonfinite condensation temperature was accepted");
            }
            finally { File.Delete(path); }
        });
    }

    private static World EvaporationCup(double temperature)
    {
        var w = new World(16, 16) { AtmosphereEnabled = false };
        w.Set(7, 10, Element.Glass, temperature);
        w.Set(9, 10, Element.Glass, temperature);
        w.Set(8, 11, Element.Glass, temperature);
        w.Set(8, 10, Element.Water, temperature);
        return w;
    }

    private static Particle SurfaceVapor(double temperature, double mass) => new()
    {
        Element = Element.Steam, MeltOrigin = Element.Water, Mass = mass,
        Energy = mass * Thermodynamics.WaterVaporEnthalpy(temperature), CondensationOffset = 100 - temperature
    };

    private static World SealedDropletPair(bool horizontal)
    {
        var w = new World(16, 16) { AtmosphereEnabled = false };
        Scenes.Rect(w, 0, 0, w.Width, w.Height, Element.Glass, 20);
        foreach (int index in new[] { (horizontal ? 8 : 7) * w.Width + (horizontal ? 7 : 8), 8 * w.Width + 8 })
        {
            var vapor = SurfaceVapor(90, Materials.Get(Element.Water).Density * World.CellVolume * .5);
            Thermodynamics.ExchangeWithReservoir(ref vapor, 20, 1, 100);
            Check(vapor.Element == Element.Water, "droplet fixture did not fully condense");
            w.Cells[index] = vapor;
        }
        return w;
    }

    private static bool RejectsInvalidSurfaceSnapshot(string path)
    {
        try { WorldFile.Load(path, 16, 16); }
        catch (InvalidDataException) { return true; }
        catch (JsonException) { return true; }
        return false;
    }
}
