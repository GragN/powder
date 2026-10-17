namespace Powder;

public static partial class SelfTests
{
    private static void RunRigidBodyTests()
    {
        Test("a sleeping rigid piece wakes and falls when its support is erased", () =>
        {
            var w = new World(80, 128) { AtmosphereEnabled = false };
            Scenes.Rect(w, 20, 60, 30, 5, Element.Stone);
            SpawnRigid(w, 26, 56, 8, 4, Element.Iron);
            for (int n = 0; n < 90; n++) w.Solids.Step(World.FixedDelta);
            double supportedY = w.Solids.Diagnostics().Single().Y;
            Check(supportedY < 60 * World.CellSize, "piece did not rest on the support");
            Scenes.Rect(w, 20, 60, 30, 5, Element.Empty);
            for (int n = 0; n < 60; n++) w.Solids.Step(World.FixedDelta);
            Check(w.Solids.Diagnostics().Single().Y > 115 * World.CellSize, "cached support left a piece hovering");
        });
        Test("burning rigid wood releases combustion matter without stale body ownership", () =>
        {
            var w = new World(48, 80) { AtmosphereEnabled = false };
            w.Solids.BeginStroke(); Scenes.Rect(w, 20, 8, 4, 4, Element.Wood, 450); w.Solids.EndStroke();
            double mass = w.Statistics().Mass;
            w.Step();
            Check(w.Solids.Count == 0 && w.Cells.Count(p => p.Element == Element.Fire) == 16, "burned matter stayed in the rigid piece");
            Check(w.Cells.All(p => p.RigidId == 0), "combustion carried stale body ownership into gas");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
        });
        Test("heated rigid motion preserves the combined material and finite-air heat budget", () =>
        {
            var w = new World(96, 120);
            w.Solids.BeginStroke(); Scenes.Rect(w, 42, 4, 8, 5, Element.Iron, 200); w.Solids.EndStroke();
            double budget = Budget(w), energy = w.Statistics().Energy;
            for (int n = 0; n < 12; n++) w.Step();
            Check(w.Solids.Diagnostics().Single().Y > 30 * World.CellSize, "heated body did not move");
            Check(w.Statistics().Energy < energy && w.Air.ExcessEnergy > 0, "moving body did not exchange heat with air");
            Near(Budget(w), budget, 1e-7);
        });
        Test("a fast rigid body cannot tunnel through a particle-wide wall", () =>
        {
            var w = new World(96, 128) { AtmosphereEnabled = false };
            Scenes.Rect(w, 60, 0, 1, 128, Element.Glass);
            SpawnRigid(w, 8, 3, 3, 4, Element.Iron); double mass = w.Statistics().Mass;
            w.Solids.SetMotion(w.Solids.Diagnostics().Single().Id, 3, 0);
            for (int n = 0; n < 6; n++) w.Solids.Step(World.FixedDelta);
            Check(w.Solids.Diagnostics().Single().X < 60 * World.CellSize, "CCD let a piece cross the wall");
            Check(w.Cells.Select((p, i) => (p, i)).Where(t => t.p.RigidId > 0).All(t => t.i % w.Width < 60), "rigid raster crossed the wall");
            Near(w.Statistics().Mass, mass, 1e-12);
        });
        Test("rigid wood floats while a dense metal piece sinks in the same liquid", () =>
        {
            var w = new World(80, 120) { AtmosphereEnabled = false };
            Scenes.Rect(w, 5, 48, 2, 72, Element.Glass); Scenes.Rect(w, 73, 48, 2, 72, Element.Glass);
            Scenes.Rect(w, 7, 60, 66, 60, Element.Water);
            SpawnRigid(w, 24, 50, 8, 6, Element.Wood); SpawnRigid(w, 48, 44, 8, 6, Element.Iron);
            var ids = w.Solids.Diagnostics().OrderBy(b => b.X).Select(b => b.Id).ToArray();
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            for (int n = 0; n < 120; n++) w.Step();
            var wood = w.Solids.Diagnostics().Single(b => b.Id == ids[0]);
            var metal = w.Solids.Diagnostics().Single(b => b.Id == ids[1]);
            Check(wood.Y / World.CellSize is > 56 and < 72, $"wood did not float near the surface: {wood}");
            Check(metal.Y > wood.Y + .05, $"dense metal did not sink: {metal}");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-10);
            Near(w.Statistics().Energy + w.EscapedEnergy, energy, 1e-7);
        });
        Test("newly frozen and solidified material becomes a falling rigid piece", () =>
        {
            var w = new World(64, 128) { AtmosphereEnabled = false };
            w.Set(16, 3, Element.Water, -10); w.Set(40, 3, Element.Lava, 1000);
            w.Solids.Synchronize();
            Check(w.Solids.Count == 2 && w.At(16, 3).Element == Element.Ice && w.At(40, 3).Element == Element.Stone,
                "phase change left an unowned solid wall");
            for (int n = 0; n < 8; n++) w.Solids.Step(World.FixedDelta);
            Check(w.Solids.Diagnostics().All(b => b.VelocityY > 1), "new solids did not fall");
        });
        Test("rigid motion and rotation survive a save while malformed membership is rejected", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-rigid-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(96, 160) { AtmosphereEnabled = false };
                SpawnRigid(w, 24, 5, 8, 5, Element.Iron);
                w.Solids.SetMotion(w.Solids.Diagnostics().Single().Id, .03, .02, .7);
                for (int n = 0; n < 4; n++) w.Solids.Step(World.FixedDelta);
                WorldFile.Save(w, path); var loaded = WorldFile.Load(path, 96, 160);
                for (int n = 0; n < 6; n++) { w.Solids.Step(World.FixedDelta); loaded.Solids.Step(World.FixedDelta); }
                var a = w.Solids.Diagnostics().Single(); var b = loaded.Solids.Diagnostics().Single();
                Near(a.X, b.X, 1e-7); Near(a.Y, b.Y, 1e-7); Near(a.Angle, b.Angle, 1e-7);
                Near(a.VelocityY, b.VelocityY, 1e-7); Near(a.AngularVelocity, b.AngularVelocity, 1e-7);
                Near(w.Statistics().Energy, loaded.Statistics().Energy, 1e-9);
                string saved = File.ReadAllText(path);
                var legacy = System.Text.Json.Nodes.JsonNode.Parse(saved)!;
                legacy["Version"] = 7; legacy.AsObject().Remove("Solids");
                foreach (var cell in legacy["Cells"]!.AsArray()) cell!.AsObject().Remove("RigidId");
                File.WriteAllText(path, legacy.ToJsonString());
                var oldScene = WorldFile.Load(path, 96, 160);
                Check(oldScene.Solids.Count == 0 && oldScene.Cells.All(p => p.RigidId == 0), "legacy walls changed their anchored state");
                Near(oldScene.Statistics().Mass, loaded.Statistics().Mass, 1e-12);
                var data = System.Text.Json.Nodes.JsonNode.Parse(saved)!;
                var samples = data["Solids"]!["Bodies"]![0]!["Samples"]!;
                samples[0]!["Cell"] = samples[1]!["Cell"]!.GetValue<int>();
                File.WriteAllText(path, data.ToJsonString());
                bool rejected = false;
                try { WorldFile.Load(path, 96, 160); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "duplicate rigid membership was accepted");
            }
            finally { File.Delete(path); }
        });
        Test("a round rigid piece rolls under contact friction without losing mass", () =>
        {
            var w = new World(256, 80) { AtmosphereEnabled = false };
            w.Solids.BeginStroke(); w.Brush(30, 71, 8, Element.Iron); w.Solids.EndStroke();
            var start = w.Solids.Diagnostics().Single(); double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            w.Solids.SetMotion(start.Id, .15, 0);
            for (int n = 0; n < 60; n++) w.Solids.Step(World.FixedDelta);
            var end = w.Solids.Diagnostics().Single();
            Check(end.X > start.X + .015 && Math.Abs(end.Angle) > .5, $"round piece did not roll: {end}");
            Check(end.Particles == start.Particles, "rotation lost rigid particles");
            int cx = (int)(end.X / World.CellSize), cy = (int)(end.Y / World.CellSize);
            for (int y = cy - 4; y <= cy + 4; y++) for (int x = cx - 4; x <= cx + 4; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= 16)
                    Check(w.At(x, y).RigidId == end.Id, "rotation left an artificial hole inside a solid disk");
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-8);
        });
        Test("rigid solids stay held during a stroke and accelerate together on release", () =>
        {
            var w = new World(80, 256) { AtmosphereEnabled = false };
            w.Solids.BeginStroke(); Scenes.Rect(w, 20, 5, 12, 4, Element.Iron);
            for (int n = 0; n < 6; n++) w.Step();
            Check(w.At(20, 5).Element == Element.Iron && w.Solids.Count == 0, "unfinished stroke fell apart");
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            w.Solids.EndStroke(); var start = w.Solids.Diagnostics().Single();
            for (int n = 0; n < 12; n++) w.Step();
            var end = w.Solids.Diagnostics().Single(); double t = 12 * World.FixedDelta;
            Near(end.VelocityY, World.Gravity * t, 2e-5);
            Near(end.Y - start.Y, .5 * World.Gravity * t * t, .0011);
            Near(end.Angle, 0, 1e-7); Check(end.Particles == 48, "a rigid piece lost its shape");
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-9);
        });
        Test("rigid mass and inertia follow material density and free fall is mass independent", () =>
        {
            var w = new World(96, 256) { AtmosphereEnabled = false };
            SpawnRigid(w, 12, 5, 8, 6, Element.Copper); SpawnRigid(w, 60, 5, 8, 6, Element.Wood);
            var before = w.Solids.Diagnostics().OrderBy(b => b.X).ToArray();
            Near(before[0].Mass, 48 * World.CellVolume * Materials.Get(Element.Copper).Density, 1e-12);
            Near(before[0].Inertia, before[0].Mass * (Math.Pow(8 * World.CellSize, 2) + Math.Pow(6 * World.CellSize, 2)) / 12, 1e-10);
            for (int n = 0; n < 10; n++) w.Step();
            var after = w.Solids.Diagnostics().OrderBy(b => b.X).ToArray();
            Near(after[0].VelocityY, after[1].VelocityY, 1e-7); Near(after[0].Y, after[1].Y, 1e-7);
        });
        Test("a rigid block lands on the bottom without losing particles or internal energy", () =>
        {
            var w = new World(64, 80) { AtmosphereEnabled = false };
            SpawnRigid(w, 24, 5, 12, 6, Element.Iron);
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            for (int n = 0; n < 120; n++) w.Solids.Step(World.FixedDelta);
            var body = w.Solids.Diagnostics().Single();
            Check(body.Particles == 72 && body.Y > 70 * World.CellSize, "block did not reach the floor");
            Check(Math.Abs(body.VelocityY) < .005 && Math.Abs(body.Angle) < .02, $"block did not settle flat: {body}");
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-9);
            Check(w.Cells.Count(p => p.RigidId == body.Id) == 72, "rasterization duplicated or dropped matter");
        });
        Test("an overhanging rigid piece tips around its support rather than hovering", () =>
        {
            var w = new World(100, 120) { AtmosphereEnabled = false };
            Scenes.Rect(w, 12, 75, 24, 45, Element.Stone);
            SpawnRigid(w, 24, 71, 32, 4, Element.Copper);
            double start = w.Solids.Diagnostics().Single().Y;
            for (int n = 0; n < 100; n++) w.Solids.Step(World.FixedDelta);
            var body = w.Solids.Diagnostics().Single();
            Check(Math.Abs(body.Angle) > .3, $"unstable body did not rotate: {body.Angle}");
            Check(body.Y > start + .01, "centre of mass did not fall off the support");
        });
        Test("rigid contacts exchange momentum between separate pieces", () =>
        {
            var w = new World(200, 512) { AtmosphereEnabled = false };
            SpawnRigid(w, 20, 4, 6, 6, Element.Iron); SpawnRigid(w, 42, 4, 6, 6, Element.Wood);
            var initial = w.Solids.Diagnostics().OrderBy(b => b.X).ToArray();
            w.Solids.SetMotion(initial[0].Id, .4, 0);
            double momentum = initial[0].Mass * .4;
            for (int n = 0; n < 10; n++) w.Solids.Step(World.FixedDelta);
            var after = w.Solids.Diagnostics();
            Check(after.Single(b => b.Id == initial[1].Id).VelocityX > .05, "lighter body did not receive an impulse");
            Near(after.Sum(b => b.Mass * b.VelocityX), momentum, 1e-7);
            Check(after.Length == 2, "contact welded independent strokes together");
        });
        Test("erasing a rigid bridge creates independent fragments and melting releases a cell", () =>
        {
            var w = new World(80, 128) { AtmosphereEnabled = false };
            SpawnRigid(w, 20, 10, 11, 3, Element.Copper);
            for (int y = 10; y < 13; y++) w.Set(25, y, Element.Empty);
            w.Solids.Synchronize(); Check(w.Solids.Count == 2, "cut did not separate the body");
            ref var p = ref w.At(20, 10);
            p.Energy = Thermodynamics.EnergyAtTemperature(p, 1200); Thermodynamics.ResolvePhase(ref p);
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            w.Solids.Synchronize();
            Check(p.Element == Element.Lava && p.RigidId == 0, "molten cell stayed rigid");
            Check(w.Solids.Diagnostics().Sum(b => b.Particles) == 29, "fragment retained removed or molten cells");
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-9);
        });
        Test("anchored strokes stay fixed while powders liquids and vapor remain independent", () =>
        {
            var w = new World(80, 128) { AtmosphereEnabled = false };
            w.Solids.BeginStroke(true); Scenes.Rect(w, 20, 8, 8, 4, Element.Stone); w.Solids.EndStroke();
            w.Solids.BeginStroke(); w.Set(40, 5, Element.Sand); w.Set(50, 5, Element.Water); w.Set(60, 5, Element.Steam); w.Solids.EndStroke();
            for (int n = 0; n < 8; n++) w.Step();
            Check(w.Solids.Count == 0 && w.At(20, 8).Element == Element.Stone, "anchored stroke was released");
            Check(w.Cells.Select((p, i) => (p, i)).Single(t => t.p.Element == Element.Sand).i / w.Width > 5, "powder stopped falling");
        });
        Test("moving rigid matter displaces liquid with a complete mass and heat budget", () =>
        {
            var w = new World(64, 96) { AtmosphereEnabled = false };
            Scenes.Rect(w, 0, 54, 64, 42, Element.Water, 35);
            SpawnRigid(w, 24, 10, 12, 6, Element.Iron);
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            for (int n = 0; n < 30; n++) w.Solids.Step(World.FixedDelta);
            Check(w.Solids.Diagnostics().Single().Y > 60 * World.CellSize, "metal did not enter the liquid");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-10);
            Near(w.Statistics().Energy + w.EscapedEnergy, energy, 1e-7);
        });
        Test("rigid matter crossing an open side is removed with its mass and heat", () =>
        {
            var w = new World(64, 128) { AtmosphereEnabled = false };
            SpawnRigid(w, 53, 3, 6, 4, Element.Glass);
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            w.Solids.SetMotion(w.Solids.Diagnostics().Single().Id, .4, 0);
            for (int n = 0; n < 12; n++) w.Solids.Step(World.FixedDelta);
            Check(w.Solids.Count == 0 && w.EscapedMass > 0, "piece did not leave the open side");
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            Near(w.Statistics().Energy + w.EscapedEnergy, energy, 1e-8);
        });
    }

    private static void SpawnRigid(World w, int x, int y, int width, int height, Element element)
    {
        w.Solids.BeginStroke(); Scenes.Rect(w, x, y, width, height, element); w.Solids.EndStroke();
    }
}
