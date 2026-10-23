namespace Powder;

public static partial class SelfTests
{
    private static double Budget(World w) => w.Statistics().Energy + w.Air.ExcessEnergy
        + w.Air.BoundaryHeat + w.EscapedEnergy - w.ExternalEnergy;

    private static void RunAtmosphereTests()
    {
        Test("a hot body transfers its lost energy into finite air", () =>
        {
            var w = new World(32, 32);
            w.Set(16, 20, Element.Copper, 200);
            double before = Budget(w), material = w.Statistics().Energy;
            w.Step();
            Check(w.At(16, 20).Temperature < 200, "body did not heat the atmosphere");
            Check(w.Air.ExcessEnergy > 0, "lost heat was not stored in air");
            Near(material - w.Statistics().Energy, w.Air.ExcessEnergy + w.Air.BoundaryHeat, 1e-10);
            Near(Budget(w), before, 1e-10);
        });
        Test("cold material absorbs energy from the air rather than a temperature timer", () =>
        {
            var w = new World(32, 32);
            w.Set(16, 12, Element.Stone, -20);
            double before = Budget(w);
            w.Step();
            Check(w.At(16, 12).Temperature > -20 && w.Air.ExcessEnergy < 0, "cold contact did not cool the air");
            Near(Budget(w), before, 1e-10);
        });
        Test("a solid interior has no automatic cooling; exposed outer faces account for lost heat", () =>
        {
            var w = new World(32, 32);
            Scenes.Rect(w, 0, 0, 32, 32, Element.Stone, 200);
            double before = w.Statistics().Energy;
            for (int n = 0; n < 20; n++) w.Step();
            Near(w.Statistics().Energy + w.Air.BoundaryHeat, before, 1e-8);
            Near(w.Air.ExcessEnergy, 0, 1e-12);
            Near(w.At(16, 16).Temperature, 200, 1e-8);
            Check(w.Air.BoundaryHeat > 0, "exposed edges did not transfer heat into outside air");
        });
        Test("a warm air plume rises and conserves heat including open-edge flux", () =>
        {
            var w = new World(64, 64);
            for (int y = 10; y < 13; y++) for (int x = 7; x < 10; x++) w.Air.SetTemperature(x, y, 80);
            double before = w.Air.ExcessEnergy, center = HeatCenter(w.Air, 1);
            for (int n = 0; n < 12; n++) w.Step();
            Check(HeatCenter(w.Air, 1) < center - w.Air.Dy * .1, "warm air did not rise");
            Check(w.Air.V.Min() < -.005, "buoyancy created no upward velocity");
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, before, 1e-10);
            Check(w.Air.DivergenceRms < .002, "pressure projection failed to enforce continuity");
        });
        Test("cold air sinks under negative buoyancy", () =>
        {
            var w = new World(64, 64);
            for (int y = 4; y < 7; y++) for (int x = 7; x < 10; x++) w.Air.SetTemperature(x, y, 0);
            double center = HeatCenter(w.Air, -1), before = w.Air.ExcessEnergy;
            for (int n = 0; n < 18; n++) w.Step();
            Check(HeatCenter(w.Air, -1) > center + w.Air.Dy * .1, "cold air did not sink");
            Check(w.Air.V.Max() > .005, "no descending cold current");
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, before, 1e-10);
        });
        Test("a heated room develops rising and returning air currents without losing thermal energy", () =>
        {
            var w = new World(64, 64);
            Scenes.Rect(w, 0, 0, 64, 4, Element.Stone);
            Scenes.Rect(w, 0, 60, 64, 4, Element.Stone);
            Scenes.Rect(w, 0, 0, 4, 64, Element.Stone);
            Scenes.Rect(w, 60, 0, 4, 64, Element.Stone);
            Scenes.Rect(w, 28, 48, 8, 4, Element.Copper, 300);
            double before = Budget(w);
            for (int n = 0; n < 120; n++) w.Step();
            Check(w.Air.V.Min() < -.005 && w.Air.V.Max() > .002, "no convective circulation in the room");
            Near(Budget(w), before, 1e-7);
            Check(w.Air.DivergenceRms < .002, "sealed-room flow is not approximately incompressible");
        });
        Test("a thin solid wall blocks air and advected heat", () =>
        {
            var w = new World(64, 64);
            Scenes.Rect(w, 32, 0, 1, 64, Element.Glass);
            w.Air.SetTemperature(5, 10, 100);
            for (int n = 0; n < 30; n++) w.Step();
            double rightHeat = 0;
            for (int y = 0; y < w.Air.Height; y++)
            for (int x = 9; x < w.Air.Width; x++) rightHeat += w.Air.Heat[y * w.Air.Width + x];
            // A wall conducts physically; the far side must not receive a transported hot blob.
            Check(Math.Abs(rightHeat) < .001, "air heat leaked through a solid wall");
            for (int y = 0; y < w.Air.Height; y++)
                Near(w.Air.U[y * (w.Air.Width + 1) + 8], 0, 1e-12);
        });
        Test("warm air can leave open edges and the outward heat is recorded", () =>
        {
            var w = new World(48, 48);
            for (int y = 0; y < w.Air.Height; y++)
            for (int x = 0; x < w.Air.Width; x++) w.Air.SetTemperature(x, y, 60);
            double before = w.Air.ExcessEnergy;
            for (int n = 0; n < 60; n++) w.Step();
            Check(w.Air.BoundaryHeat > before * .05, "open atmosphere did not release heat");
            Near(w.Air.ExcessEnergy + w.Air.BoundaryHeat, before, 1e-10);
            for (int x = 0; x < w.Air.Width; x++) Near(w.Air.V[w.Air.Height * w.Air.Width + x], 0, 1e-12);
        });
        Test("particles crossing left, right and top edges disappear with an energy and mass budget", () =>
        {
            foreach (int side in new[] { -1, 1, 0 })
            {
                var w = new World(32, 32) { AtmosphereEnabled = false };
                int x = side < 0 ? 1 : side > 0 ? 30 : 16, y = side == 0 ? 1 : 8;
                w.Set(x, y, side == 0 ? Element.Steam : Element.Sand);
                if (side == 0) w.At(x, y).VelocityY = -240;
                else w.At(x, y).VelocityX = side * 240;
                var before = w.Statistics();
                w.Step();
                Check(w.Count == 0, "particle remained on an open border");
                Near(w.EscapedMass, before.Mass, 1e-15); Near(w.EscapedEnergy, before.Energy, 1e-12);
            }
        });
        Test("the bottom is a solid boundary while liquid can drain through a side", () =>
        {
            var w = new World(32, 32) { AtmosphereEnabled = false };
            w.Set(16, 30, Element.Sand); w.At(16, 30).VelocityY = 1000;
            w.Step();
            Check(w.At(16, 31).Element == Element.Sand, "sand left through the floor");
            Near(w.EscapedMass, 0, 1e-15);
            w.Clear(); w.Set(0, 31, Element.Water);
            double mass = w.Statistics().Mass;
            for (int n = 0; n < 20; n++) w.Step();
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-15);
            Check(w.EscapedMass > 0, "side edge acts as a wall to liquid");
        });
        Test("gas markers are entrained horizontally by the atmosphere", () =>
        {
            var w = new World(64, 64);
            Array.Fill(w.Air.U, .3);
            w.Set(16, 32, Element.Smoke, 20);
            for (int n = 0; n < 8; n++) w.Step();
            int index = Array.FindIndex(w.Cells, p => p.Element == Element.Smoke);
            Check(index >= 0 && index % w.Width > 24, "gas did not follow the lateral current");
        });
        Test("moving obstacles preserve atmospheric excess heat", () =>
        {
            var w = new World(32, 32);
            w.Air.SetTemperature(4, 4, 80);
            double before = w.Air.ExcessEnergy;
            w.Set(16, 16, Element.Stone);
            w.Air.UpdateObstacles(w);
            Near(w.Air.ExcessEnergy, before, 1e-12);
            w.Set(16, 16, Element.Empty); w.Air.UpdateObstacles(w);
            Near(w.Air.ExcessEnergy, before, 1e-12);
        });
        Test("saves retain temperature, air velocity and horizontal particle inertia", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-air-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(32, 32);
                w.Air.SetTemperature(4, 5, 90);
                for (int n = 0; n < 5; n++) w.Step();
                w.Set(8, 8, Element.Steam); w.At(8, 8).VelocityX = 10; w.At(8, 8).VelocityY = -8;
                w.At(8, 8).TravelX = -.25; w.At(8, 8).Travel = -.5;
                WorldFile.Save(w, path);
                var restored = WorldFile.Load(path, 32, 32);
                Check(restored.Air.Heat.SequenceEqual(w.Air.Heat) && restored.Air.U.SequenceEqual(w.Air.U)
                    && restored.Air.V.SequenceEqual(w.Air.V), "air state was reset on load");
                Near(restored.At(8, 8).TravelX, -.25, 1e-12); Near(restored.At(8, 8).VelocityY, -8, 1e-12);
                Near(restored.Air.BoundaryHeat, w.Air.BoundaryHeat, 1e-12);
            }
            finally { File.Delete(path); }
        });
    }

    private static double HeatCenter(AirField air, int sign)
    {
        double total = 0, moment = 0;
        for (int i = 0; i < air.Heat.Length; i++)
        {
            double weight = Math.Max(0, sign * air.Heat[i]);
            total += weight; moment += weight * (i / air.Width + .5) * air.Dy;
        }
        return moment / Math.Max(1e-30, total);
    }
}
