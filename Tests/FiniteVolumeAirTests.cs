namespace Powder;

public static partial class SelfTests
{
    private static void RunFiniteVolumeAirTests()
    {
        Test("finite-volume geometry covers exactly the free particle area including ragged edges", () =>
        {
            var w = new World(35, 29);
            w.Air.UpdateObstacles(w);
            Near(w.Air.TotalAirVolume, 35 * 29 * World.CellVolume, 1e-15);
            Check(w.Air.ControlVolumeCount == w.Air.Width * w.Air.Height, "empty space was unnecessarily refined");
            for (int y = 0; y < w.Height; y++) for (int x = 0; x < w.Width; x++)
                if ((x - 17) * (x - 17) + (y - 16) * (y - 16) is >= 49 and <= 64 && y >= 14)
                    w.Set(x, y, Element.Glass);
            w.Air.UpdateObstacles(w);
            int empty = 0;
            for (int y = 0; y < w.Height; y++) for (int x = 0; x < w.Width; x++)
            {
                bool gas = w.At(x, y).Element == Element.Empty;
                Check((w.Air.VolumeIndexAtFine(x, y) >= 0) == gas, "boundary left a hole or placed air inside material");
                if (gas) empty++;
            }
            Near(w.Air.TotalAirVolume, empty * World.CellVolume, 1e-15);
            Near(w.Air.CapacityAtFine(0, 0), 16 * World.CellVolume * AirField.Density * AirField.SpecificHeat, 1e-14);
        });
        Test("finite-volume wind and heat pass through a particle-wide channel", () =>
        {
            var w = new World(32, 32);
            Scenes.Rect(w, 0, 0, 32, 32, Element.Glass);
            for (int x = 0; x < w.Width; x++) w.Set(x, 17, Element.Empty);
            w.Air.UpdateObstacles(w);
            w.Air.SetTemperatureAtFine(8, 17, 80); w.Air.SetUniformVelocity(.1, 0);
            double before = FineHeatCenterX(w), heat = w.Air.ExcessEnergy;
            for (int n = 0; n < 6; n++) w.Air.Step(World.FixedDelta);
            Check(w.Air.NormalVelocityAtFineFace(16, 17, false) > .005, "a real open slit was closed to flow");
            Check(FineHeatCenterX(w) > before + .5, "heat did not travel with air through the slit");
            Check(w.Air.MaximumDivergence < .001, "small air volumes violate continuity");
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, heat, 1e-11);
        });
        Test("finite-volume regions on opposite sides of an embedded wall never exchange air heat", () =>
        {
            var w = new World(32, 32);
            Scenes.Rect(w, 17, 0, 1, 32, Element.Glass);
            w.Air.UpdateObstacles(w);
            w.Air.SetTemperatureAtFine(16, 16, 80); w.Air.SetUniformVelocity(.1, 0);
            double heat = w.Air.ExcessEnergy;
            for (int n = 0; n < 18; n++) w.Air.Step(World.FixedDelta);
            double rightHeat = 0;
            for (int y = 0; y < w.Height; y++)
            {
                Near(w.Air.NormalVelocityAtFineFace(17, y, false), 0, 1e-12);
                Near(w.Air.NormalVelocityAtFineFace(18, y, false), 0, 1e-12);
                for (int x = 18; x < w.Width; x++) rightHeat += (w.Air.TemperatureAtFine(x, y) - 20)
                    * World.CellVolume * AirField.Density * AirField.SpecificHeat;
            }
            Near(rightHeat, 0, 1e-12);
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, heat, 1e-11);
        });
        Test("finite-volume narrow passages support buoyant rise and returning air", () =>
        {
            var w = new World(32, 36);
            Scenes.Rect(w, 0, 0, w.Width, w.Height, Element.Glass);
            for (int y = 0; y <= 31; y++) { w.Set(13, y, Element.Empty); w.Set(15, y, Element.Empty); }
            w.Set(14, 31, Element.Empty); w.Air.UpdateObstacles(w);
            for (int y = 25; y <= 31; y++) w.Air.SetTemperatureAtFine(13, y, 80);
            double heat = w.Air.ExcessEnergy;
            for (int n = 0; n < 24; n++) w.Air.Step(World.FixedDelta);
            Check(w.Air.VelocityAtFine(13, 16).Y < -.001, "heated narrow leg did not rise");
            Check(w.Air.VelocityAtFine(15, 16).Y > .0001, "cold narrow leg supplied no return flow");
            Check(w.Air.MaximumDivergence < .001, "narrow circulating air is not approximately incompressible");
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, heat, 1e-11);
        });
        Test("finite-volume conduction follows the analytical finite-capacity cooling curve", () =>
        {
            var w = new World(16, 16);
            Scenes.Rect(w, 0, 0, 16, 16, Element.Glass);
            w.Set(8, 8, Element.Empty); w.Set(9, 8, Element.Empty); w.Air.UpdateObstacles(w);
            w.Air.SetTemperatureAtFine(8, 8, 80);
            double heat = w.Air.ExcessEnergy, capacity = w.Air.CapacityAtFine(8, 8);
            for (int n = 0; n < 400; n++) w.Air.Step(.00025);
            double exact = 50 + 30 * Math.Exp(-2 * AirField.Conductivity * World.Depth * .1 / capacity);
            Near(w.Air.TemperatureAtFine(8, 8), exact, .03);
            Near((w.Air.TemperatureAtFine(8, 8) + w.Air.TemperatureAtFine(9, 8)) * .5, 50, 1e-10);
            Near(w.Air.ExcessEnergy, heat, 1e-12);
        });
        Test("finite-volume saves include paused geometry edits and migrate version six pocket energy", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-fvm-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(32, 32);
                w.Air.SetTemperature(3, 3, 80); double heat = w.Air.ExcessEnergy;
                w.Set(15, 15, Element.Copper); // no step before saving
                WorldFile.Save(w, path);
                var loaded = WorldFile.Load(path, 32, 32);
                Check(loaded.Air.VolumeIndexAtFine(15, 15) < 0, "paused edit was not included in saved topology");
                Near(loaded.Air.ExcessEnergy, heat, 1e-12);
                var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                data["Version"] = 6;
                var air = data["Air"]!.AsObject();
                air.Remove("VolumeHeat"); air.Remove("FaceVelocity"); air.Remove("TrappedHeat");
                var fine = new double[32 * 32]; fine[15 * 32 + 14] = .003;
                air["PocketHeat"] = System.Text.Json.JsonSerializer.SerializeToNode(fine);
                air["Heat"] = System.Text.Json.JsonSerializer.SerializeToNode(new double[w.Air.Heat.Length]);
                File.WriteAllText(path, data.ToJsonString());
                loaded = WorldFile.Load(path, 32, 32);
                Near(loaded.Air.ExcessEnergy, .003, 1e-12);
                Near(loaded.Air.TemperatureAtFine(14, 15), 20 + .003 / loaded.Air.CapacityAtFine(14, 15), 1e-9);
            }
            finally { File.Delete(path); }
        });
    }

    private static double FineHeatCenterX(World w)
    {
        double total = 0, moment = 0;
        for (int y = 0; y < w.Height; y++) for (int x = 0; x < w.Width; x++)
        {
            if (w.Air.VolumeIndexAtFine(x, y) < 0) continue;
            double weight = Math.Max(0, w.Air.TemperatureAtFine(x, y) - 20);
            total += weight; moment += weight * (x + .5);
        }
        return moment / Math.Max(1e-30, total);
    }
}
