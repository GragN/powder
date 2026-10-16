namespace Powder;

public static partial class SelfTests
{
    private static void RunAirPocketTests()
    {
        Test("every air cell touching an unaligned hot surface receives heat locally", () =>
        {
            foreach (var size in new[] { (32, 32), (35, 29) })
            {
                var w = new World(size.Item1, size.Item2);
                w.Set(15, 15, Element.Copper, 200);
                w.Air.UpdateObstacles(w);
                double budget = Budget(w);
                w.Air.ExchangeSurface(w, 15, 15, World.FixedDelta);
                foreach (var cell in new[] { (14, 15), (16, 15), (15, 14), (15, 16) })
                    Check(w.Air.TemperatureAtFine(cell.Item1, cell.Item2) > 20,
                        $"air at {cell} was skipped because its coarse block contains material");
                Check(w.Air.PocketHeat.Any(h => h > 0), "boundary gas has no finite thermal energy");
                Near(Budget(w), budget, 1e-10);
            }
        });
        Test("a one-cell closed air pocket warms through its finite heat capacity", () =>
        {
            var w = new World(16, 16);
            Scenes.Rect(w, 0, 0, 16, 16, Element.Stone, 100);
            w.Set(8, 8, Element.Empty);
            double budget = Budget(w);
            for (int n = 0; n < 12; n++) w.Step();
            Check(w.Air.TemperatureAtFine(8, 8) > 40, "a closed gas pocket stayed at room temperature");
            Check(w.Air.TemperatureAtFine(8, 8) <= 100, "contact overshot the hottest body");
            Check(w.Air.Blocked.All(b => b), "test did not isolate a sub-grid air pocket");
            Near(Budget(w), budget, 1e-8);
        });
        Test("sub-grid air conducts across open faces and respects a one-cell wall", () =>
        {
            foreach (bool wall in new[] { false, true })
            {
                var w = new World(16, 16);
                Scenes.Rect(w, 0, 0, 16, 16, Element.Glass);
                int x = wall ? 10 : 9;
                w.Set(8, 8, Element.Empty); w.Set(x, 8, Element.Empty);
                w.Air.UpdateObstacles(w);
                w.Air.PocketHeat[8 * w.Width + 8] = 60 * AirField.PocketCapacity;
                double heat = w.Air.ExcessEnergy;
                for (int n = 0; n < 12; n++) w.Air.Step(World.FixedDelta);
                double cold = w.Air.TemperatureAtFine(x, 8), hot = w.Air.TemperatureAtFine(8, 8);
                if (wall) { Near(cold, 20, 1e-12); Near(hot, 80, 1e-12); }
                else Check(cold > 20 && hot < 80 && hot >= cold,
                    "open fine air cells did not conduct toward equilibrium");
                Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, heat, 1e-12);
            }
        });
        Test("boundary air exchanges heat with the resolved atmosphere conservatively", () =>
        {
            var w = new World(32, 32);
            w.Set(15, 15, Element.Stone);
            w.Air.UpdateObstacles(w);
            w.Air.PocketHeat[12 * w.Width + 15] = 60 * AirField.PocketCapacity;
            double heat = w.Air.ExcessEnergy;
            for (int n = 0; n < 12; n++) w.Air.Step(World.FixedDelta);
            Check(w.Air.Heat.Any(h => h > 0), "heat remained trapped behind a coarse-grid interface");
            Check(w.Air.TemperatureAtFine(15, 12) < 80, "the boundary cell did not give heat to neighbouring air");
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, heat, 1e-11);
        });
        Test("opening and closing a narrow air passage updates thermal contact", () =>
        {
            var w = new World(16, 16);
            Scenes.Rect(w, 0, 0, 16, 16, Element.Glass);
            w.Set(8, 8, Element.Empty); w.Set(10, 8, Element.Empty);
            w.Air.UpdateObstacles(w);
            var coarseMask = w.Air.Blocked.ToArray();
            w.Air.PocketHeat[8 * w.Width + 8] = 60 * AirField.PocketCapacity;
            w.Air.Step(World.FixedDelta);
            w.Set(9, 8, Element.Empty); w.Air.UpdateObstacles(w);
            Check(w.Air.Blocked.SequenceEqual(coarseMask), "fixture changed the resolved velocity domain");
            for (int n = 0; n < 12; n++) w.Air.Step(World.FixedDelta);
            Check(w.Air.TemperatureAtFine(10, 8) > 20, "newly opened air passage did not conduct heat");
            w.Set(9, 8, Element.Glass); w.Air.UpdateObstacles(w);
            Array.Clear(w.Air.PocketHeat);
            w.Air.PocketHeat[8 * w.Width + 8] = 60 * AirField.PocketCapacity;
            double heat = w.Air.ExcessEnergy;
            for (int n = 0; n < 12; n++) w.Air.Step(World.FixedDelta);
            Near(w.Air.TemperatureAtFine(10, 8), 20, 1e-12);
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, heat, 1e-12);
        });
        Test("moving material preserves boundary-air heat when blocks close and reopen", () =>
        {
            var w = new World(32, 32);
            w.Air.SetTemperature(2, 2, 80);
            double heat = w.Air.ExcessEnergy;
            for (int n = 0; n < 16; n++)
            {
                w.Set(8 + n % 4, 8 + n / 4, Element.Stone);
                w.Air.UpdateObstacles(w);
                Near(w.Air.ExcessEnergy, heat, 1e-12);
            }
            for (int n = 0; n < 16; n++)
            {
                w.Set(8 + n % 4, 8 + n / 4, Element.Empty);
                w.Air.UpdateObstacles(w);
                Near(w.Air.ExcessEnergy, heat, 1e-12);
            }
            Check(w.Air.PocketHeat.All(h => h == 0), "reopened air retained duplicate fine-grid heat");
        });
        Test("scene files preserve boundary-air heat and accept older coarse-air scenes", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-air-pocket-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(32, 32);
                w.Set(15, 15, Element.Copper, 200); w.Step();
                Check(w.Air.PocketHeat.Any(h => h > 0), "save fixture has no heated boundary air");
                WorldFile.Save(w, path);
                var loaded = WorldFile.Load(path, 32, 32);
                Check(loaded.Air.PocketHeat.SequenceEqual(w.Air.PocketHeat), "saved boundary air was lost");
                Near(Budget(loaded), Budget(w), 1e-10);
                Near(loaded.Air.TemperatureAtFine(14, 15), w.Air.TemperatureAtFine(14, 15), 1e-12);
                var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                data["Air"]!.AsObject().Remove("PocketHeat");
                File.WriteAllText(path, data.ToJsonString());
                bool rejected = false;
                try { WorldFile.Load(path, 32, 32); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "a new scene silently lost its required boundary-air data");
                data["Version"] = 5;
                File.WriteAllText(path, data.ToJsonString());
                loaded = WorldFile.Load(path, 32, 32);
                Check(loaded.Air.PocketHeat.All(h => h == 0), "legacy scene invented boundary energy");
                loaded.Clear();
                Near(loaded.Air.ExcessEnergy, 0, 1e-12);
            }
            finally { File.Delete(path); }
        });
    }
}
