namespace Powder;

public static partial class SelfTests
{
    private static void RunBoilingBehaviorTests()
    {
        Test("the first latent heat produces fractional vapor in one simulation tick", () =>
        {
            foreach (var origin in EvaporatingOrigins.Append(Element.Water))
            {
                double boiling = VaporProperties.BoilingPoint(origin);
                var w = new World(16, 16) { AtmosphereEnabled = false };
                Scenes.Rect(w, 0, 0, w.Width, w.Height, origin, boiling);
                double sourceMass = w.At(8, 8).Mass;
                double initialMass = w.Statistics().Mass, initialBudget = Budget(w);
                double expectedVapor = sourceMass * .01;
                w.HeatBrush(8, 8, 0, 1, expectedVapor * VaporProperties.LatentHeat(origin));
                w.Step();
                double vapor = VaporMass(w);
                Check(vapor > expectedVapor * .95, $"{origin} waited for an entire cell to evaporate");
                Near(vapor, expectedVapor, 1e-10);
                Check(w.Cells.Any(p => Materials.Get(p.Element).State == MatterState.Liquid),
                    $"{origin} replaced all residual liquid with gas");
                Near(w.Statistics().Mass + w.EscapedMass, initialMass, 1e-11);
                Near(Budget(w), initialBudget, 1e-6);
            }
        });
        Test("a saturated submerged bubble reaches the surface without excavating liquid cells", () =>
        {
            var w = BoilingBasin(32, 64, 8, 100);
            int bubbleIndex = 50 * w.Width + 16;
            w.Bubbles[bubbleIndex] = SaturatedWaterBubble();
            w.RebuildBubbleIndices();
            double bubbleMass = w.Bubbles[bubbleIndex].Mass;
            double initialMass = w.Statistics().Mass, initialBudget = Budget(w);
            int liquidCells = w.Cells.Count(p => p.Element == Element.Water);
            for (int n = 0; n < 90; n++) w.Step();
            Check(w.Bubbles.Sum(p => p.Mass) < bubbleMass * .01,
                "a bubble remained trapped beneath saturated water");
            Check(w.Cells.Any(p => p.Element == Element.Steam) || w.EscapedMass > 0,
                "the bubble vanished instead of entering the atmosphere");
            Check(w.Cells.Count(p => p.Element == Element.Water) == liquidCells,
                "bubble rise excavated a column through the water");
            Near(w.Statistics().Mass + w.EscapedMass, initialMass, 1e-11);
            Near(Budget(w), initialBudget, 1e-7);
        });
        Test("fractional evaporation retains dissolved salt in the liquid", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 0, w.Width, w.Height, Element.Water, 100);
            ref var source = ref w.At(8, 8);
            source.DissolvedSalt = source.Mass * .05;
            source.Energy = Thermodynamics.EnergyAtTemperature(source, 100);
            double salt = source.DissolvedSalt;
            double initialMass = w.Statistics().Mass, initialBudget = Budget(w);
            double expectedVapor = (source.Mass - source.DissolvedSalt) * .01;
            w.HeatBrush(8, 8, 0, 1, expectedVapor * Thermodynamics.Vaporization);
            w.Step();
            Near(VaporMass(w), expectedVapor, 1e-10);
            Near(w.Bubbles.Sum(p => p.DissolvedSalt), 0, 1e-15);
            Near(w.Cells.Sum(p => p.DissolvedSalt), salt, 1e-14);
            Near(w.Statistics().Mass + w.EscapedMass, initialMass, 1e-11);
            Near(Budget(w), initialBudget, 1e-7);
        });
        Test("complete evaporation leaves nonvolatile salt out of the vapor", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
            w.Set(8, 8, Element.Water, 100);
            ref var p = ref w.At(8, 8);
            double water = p.Mass, salt = water * .05;
            p.Mass += salt; p.DissolvedSalt = salt; p.Energy += salt * 880 * 100;
            double mass = w.Statistics().Mass, budget = Budget(w);
            w.HeatBrush(8, 8, 0, 1, water * (Thermodynamics.Vaporization + 1996 * 10) + salt * 880 * 10);
            w.Step();
            Near(w.Cells.Where(q => q.Element == Element.Salt).Sum(q => q.Mass), salt, 1e-12);
            Check(w.Cells.Concat(w.Bubbles).Where(q => q.Element is Element.Steam or Element.Vapor)
                .All(q => q.DissolvedSalt == 0), "nonvolatile salt entered steam");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12); Near(Budget(w), budget, 1e-7);
        });
        Test("a small bubble condenses in subcooled water and returns its mass and latent heat", () =>
        {
            var w = BoilingBasin(32, 64, 8, 20);
            // Isolate condensation of the injected bubble from the independently
            // tested surface evaporation above the basin.
            Scenes.Rect(w, 5, 7, w.Width - 10, 1, Element.Glass, 20);
            int bubbleIndex = 52 * w.Width + 16;
            w.Bubbles[bubbleIndex] = SaturatedWaterBubble();
            w.RebuildBubbleIndices();
            double bubbleMass = w.Bubbles[bubbleIndex].Mass;
            double initialMass = w.Statistics().Mass, initialBudget = Budget(w);
            double liquidMass = w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass);
            for (int n = 0; n < 90; n++) w.Step();
            Check(VaporMass(w) < bubbleMass * .01, "cold water did not condense the submerged bubble");
            Check(w.Cells.Where(p => p.Element == Element.Water).Sum(p => p.Mass) > liquidMass + bubbleMass * .95,
                "condensate was discarded instead of returning to liquid");
            Near(w.EscapedMass, 0, 1e-15);
            Near(w.Statistics().Mass, initialMass, 1e-11);
            Near(Budget(w), initialBudget, 1e-7);
        });
        Test("localized boiling settles without a persistent bowl in the free surface", () =>
        {
            var w = BoilingBasin(64, 64, 24, 100);
            double initialMass = w.Statistics().Mass, initialBudget = Budget(w);
            for (int n = 0; n < 90; n++)
            {
                w.HeatBrush(32, 53, 3, 60);
                w.Step();
            }
            double bowl = 0;
            for (int n = 0; n < 120; n++)
            {
                w.Step();
                if (n >= 60) bowl += BasinBowlDepth(w);
            }
            Check(bowl / 60 < 2.5, $"boiling left a persistent surface depression of {bowl / 60:F2} cells");
            Near(w.Statistics().Mass + w.EscapedMass, initialMass, 1e-10);
            Near(Budget(w), initialBudget, 1e-6);
        });
        Test("colliding bubbles retain their substances when one is blocked by a wall", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 0, 16, 16, Element.Glass, 350);
            Scenes.Rect(w, 1, 1, 14, 14, Element.Oil, 350);
            w.Set(9, 9, Element.Glass, 350);
            int oilIndex = 10 * w.Width + 8, waterIndex = oilIndex + 1;
            w.Bubbles[oilIndex] = Make(Element.Oil, 351);
            w.Bubbles[oilIndex].VelocityX = 120;
            w.Bubbles[waterIndex] = SaturatedWaterBubble();
            w.Bubbles[waterIndex].VelocityY = -120;
            w.RebuildBubbleIndices();
            double oil = w.Bubbles[oilIndex].Mass, water = w.Bubbles[waterIndex].Mass;
            double budget = Budget(w), mass = w.Statistics().Mass;
            w.Step();
            Near(w.Bubbles.Where(p => p.Element == Element.Vapor && p.MeltOrigin == Element.Oil).Sum(p => p.Mass), oil, 1e-12);
            Near(w.Bubbles.Where(p => p.Element == Element.Steam).Sum(p => p.Mass), water, 1e-12);
            Near(Budget(w), budget, 1e-7); Near(w.Statistics().Mass, mass, 1e-12);
        });
        Test("scene files retain submerged vapor mass, enthalpy and fractional travel", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-bubble-{Guid.NewGuid()}.json");
            try
            {
                var w = BoilingBasin(32, 64, 8, 100);
                int index = 48 * w.Width + 16;
                w.Bubbles[index] = SaturatedWaterBubble();
                w.Bubbles[index].Travel = -.45;
                w.Bubbles[index].TravelX = .12;
                w.RebuildBubbleIndices();
                double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
                WorldFile.Save(w, path);
                var loaded = WorldFile.Load(path, w.Width, w.Height);
                Check(loaded.Bubbles[index].Element == Element.Steam, "saved bubble was lost");
                Near(loaded.Bubbles[index].Mass, w.Bubbles[index].Mass, 1e-15);
                Near(loaded.Bubbles[index].Energy, w.Bubbles[index].Energy, 1e-12);
                Near(loaded.Bubbles[index].Travel, -.45, 1e-15);
                Near(loaded.Bubbles[index].TravelX, .12, 1e-15);
                Near(loaded.Statistics().Mass, mass, 1e-12);
                Near(loaded.Statistics().Energy, energy, 1e-8);
                var malformed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                malformed["Bubbles"]![0]!["Salt"] = w.Bubbles[index].Mass * .1;
                File.WriteAllText(path, malformed.ToJsonString());
                bool rejected = false;
                try { WorldFile.Load(path, w.Width, w.Height); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "nonvolatile salt was accepted in a dispersed gas parcel");
                loaded.Clear();
                Check(loaded.Bubbles.All(p => p.Element == Element.Empty && p.Mass == 0),
                    "clearing the scene left invisible vapor behind");
            }
            finally { File.Delete(path); }
        });
    }

    private static World BoilingBasin(int width, int height, int surface, double temperature)
    {
        var w = new World(width, height) { AtmosphereEnabled = false };
        Scenes.Rect(w, 3, surface - 2, 2, height - surface, Element.Glass, temperature);
        Scenes.Rect(w, width - 5, surface - 2, 2, height - surface, Element.Glass, temperature);
        Scenes.Rect(w, 3, height - 4, width - 6, 2, Element.Glass, temperature);
        Scenes.Rect(w, 5, surface, width - 10, height - 4 - surface, Element.Water, temperature);
        return w;
    }

    private static double VaporMass(World w) =>
        w.Bubbles.Where(p => p.Element is Element.Steam or Element.Vapor).Sum(p => p.Mass)
        + w.Cells.Where(p => p.Element is Element.Steam or Element.Vapor).Sum(p => p.Mass);

    private static Particle SaturatedWaterBubble()
    {
        var bubble = Make(Element.Steam, 100);
        bubble.MeltOrigin = Element.Water;
        return bubble;
    }

    private static double BasinBowlDepth(World w)
    {
        double centre = 0, edges = 0; int centreCount = 0, edgeCount = 0;
        for (int x = 7; x < w.Width - 7; x++)
        {
            int surface = -1;
            for (int y = 0; y < w.Height; y++)
                if (w.At(x, y).Element == Element.Water) { surface = y; break; }
            Check(surface >= 0, "a boiling plume left an empty water column");
            if (x >= w.Width * 3 / 8 && x < w.Width * 5 / 8)
            { centre += surface; centreCount++; }
            if (x < w.Width / 4 || x >= w.Width * 3 / 4)
            { edges += surface; edgeCount++; }
        }
        return centre / centreCount - edges / edgeCount;
    }
}
