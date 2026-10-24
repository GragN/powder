namespace Powder;

public static partial class SelfTests
{
    private static void RunSteamAirTests()
    {
        Test("isothermal steam drives an air plume without heating the atmosphere", () =>
        {
            var w = SteamCloud();
            double budget = Budget(w), mass = w.Statistics().Mass;
            double center = SteamCenterY(w);
            for (int n = 0; n < 30; n++) w.Step();
            Check(w.Air.V.Min() < -.001, "steam produced no upward air flow at equal temperature");
            Check(w.Air.U.Any(v => Math.Abs(v) > .0001), "steam did not entrain surrounding air");
            Check(SteamCenterY(w) < center - .2, "steam did not rise with its induced plume");
            Near(w.Air.ExcessEnergy, 0, 1e-10);
            Near(Budget(w), budget, 1e-9);
            Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            Check(w.Air.DivergenceRms < .002, "steam flow violates pressure continuity");
        });
        Test("steam-induced air buoyancy grows with vapor mass", () =>
        {
            double previous = 0;
            foreach (double amount in new[] { .001, .1, 1.0 })
            {
                var w = SteamCloud(amount);
                w.Air.Step(w, World.FixedDelta);
                double up = -w.Air.V.Min();
                Check(up > previous, "a lighter steam cloud drove a stronger plume");
                previous = up;
            }
        });
        Test("two-way steam drag conserves momentum and dissipates relative kinetic energy", () =>
        {
            foreach (double loading in new[] { .001, 1.0, 10_000.0 })
            {
                var w = new World(32, 32);
                w.At(16, 16) = SurfaceVapor(20, Materials.Get(Element.Steam).Density * World.CellVolume * loading);
                w.At(16, 16).VelocityX = 100; w.At(16, 16).VelocityY = -50;
                double mx = CoupledMomentum(w, false), my = CoupledMomentum(w, true), energy = CoupledKineticEnergy(w);
                w.Air.CoupleSteamMomentum(w, World.FixedDelta);
                Near(CoupledMomentum(w, false), mx, 1e-14);
                Near(CoupledMomentum(w, true), my, 1e-14);
                Check(CoupledKineticEnergy(w) <= energy * (1 + 1e-12), "drag injected mechanical energy");
                Check(w.At(16, 16).VelocityX is > 0 and < 100, "steam failed to share horizontal momentum");
                Check(w.Air.NormalVelocityAtFineFace(16, 16, false) > 0, "moving steam did not push air");
            }
        });
        Test("air loses momentum when it entrains stationary steam", () =>
        {
            var w = new World(32, 32);
            w.At(16, 16) = SurfaceVapor(20, 1e-7);
            w.Air.SetUniformVelocity(.2, 0);
            double momentum = CoupledMomentum(w, false), energy = CoupledKineticEnergy(w);
            w.Air.CoupleSteamMomentum(w, World.FixedDelta);
            Check(w.At(16, 16).VelocityX > 0, "air did not entrain stationary steam");
            Check(w.Air.NormalVelocityAtFineFace(16, 16, false) < .2, "entrainment had no reaction on air");
            Near(CoupledMomentum(w, false), momentum, 1e-13);
            Check(CoupledKineticEnergy(w) <= energy * (1 + 1e-12), "entrainment injected kinetic energy");
        });
        Test("a steam plume develops return flow in a sealed isothermal room", () =>
        {
            var w = SteamCloud();
            Scenes.Rect(w, 0, 0, 64, 4, Element.Glass, 20);
            Scenes.Rect(w, 0, 60, 64, 4, Element.Glass, 20);
            Scenes.Rect(w, 0, 0, 4, 64, Element.Glass, 20);
            Scenes.Rect(w, 60, 0, 4, 64, Element.Glass, 20);
            double mass = w.Statistics().Mass, budget = Budget(w);
            for (int n = 0; n < 90; n++) w.Step();
            Check(w.Air.V.Min() < -.001 && w.Air.V.Max() > .001, "steam created no rising/returning circulation");
            Near(w.EscapedMass, 0, 1e-15); Near(w.Air.ExcessEnergy, 0, 1e-9);
            Near(w.Statistics().Mass, mass, 1e-12); Near(Budget(w), budget, 1e-8);
            Check(w.Air.DivergenceRms < .002, "closed steam circulation violates continuity");
        });
        Test("uniform steam in a sealed room remains in hydrostatic equilibrium", () =>
        {
            var w = new World(32, 32);
            Scenes.Rect(w, 0, 0, 32, 32, Element.Glass, 20);
            for (int y = 4; y < 28; y++) for (int x = 4; x < 28; x++)
                w.At(x, y) = SurfaceVapor(20, Materials.Get(Element.Steam).Density * World.CellVolume);
            for (int n = 0; n < 30; n++) w.Step();
            Check(w.Air.U.Concat(w.Air.V).All(v => Math.Abs(v) < 1e-6), "uniform gas spontaneously stirred");
            Check(w.Cells.Where(p => p.Element == Element.Steam).All(p => Math.Abs(p.VelocityX) < .001
                && Math.Abs(p.VelocityY) < .001), "steam drifted through its own balanced pressure field");
        });
        Test("steam-induced flow cannot cross a one-cell solid wall", () =>
        {
            var w = new World(64, 64);
            Scenes.Rect(w, 32, 0, 1, 64, Element.Glass, 20);
            PutSteamCloud(w, 20, 40, 1);
            for (int n = 0; n < 20; n++) w.Step();
            Check(w.Air.V.Min() < -.001, "fixture did not create a steam plume");
            for (int y = 0; y < 64; y++)
            {
                Near(w.Air.NormalVelocityAtFineFace(32, y, false), 0, 1e-12);
                Near(w.Air.VelocityAtFine(48, y).X, 0, 1e-9);
                Near(w.Air.VelocityAtFine(48, y).Y, 0, 1e-9);
            }
        });
        Test("submerged steam does not inject momentum into disconnected air", () =>
        {
            var w = new World(32, 32);
            Scenes.Rect(w, 8, 8, 16, 16, Element.Water, 20);
            int index = 16 * w.Width + 16;
            w.Bubbles[index] = SurfaceVapor(20, 1e-8); w.Bubbles[index].VelocityX = 100;
            w.RebuildBubbleIndices(); w.Air.UpdateObstacles(w);
            w.Air.CoupleSteamMomentum(w, World.FixedDelta);
            w.Air.Step(w, World.FixedDelta);
            Near(w.Bubbles[index].VelocityX, 100, 0);
            Check(!w.Air.Active && w.Air.U.All(v => v == 0) && w.Air.V.All(v => v == 0),
                "submerged bubble forced air through liquid");
        });
        Test("liquid condensate is not counted as buoyant vapor", () =>
        {
            var w = SteamCloud();
            w.Air.Step(w, World.FixedDelta);
            Check(w.Air.V.Min() < 0, "fixture did not start with steam-induced flow");
            for (int i = 0; i < w.Cells.Length; i++)
            {
                ref var p = ref w.Cells[i];
                if (p.Element != Element.Steam) continue;
                p.Energy = p.Mass * Thermodynamics.WaterLiquidEnthalpy(20);
                Thermodynamics.ResolvePhase(ref p);
            }
            w.Air.SetUniformVelocity(0, 0); // Remove previous inertia, isolate forcing.
            w.Air.UpdateObstacles(w); w.Air.Step(w, World.FixedDelta);
            Check(!w.Air.Active && w.Air.U.All(v => v == 0) && w.Air.V.All(v => v == 0),
                "condensate was treated as light vapor");
        });
        Test("saved steam resumes its induced air circulation and clearing removes it", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-steam-air-{Guid.NewGuid()}.json");
            try
            {
                var w = SteamCloud();
                for (int n = 0; n < 8; n++) w.Step();
                WorldFile.Save(w, path);
                var loaded = WorldFile.Load(path, w.Width, w.Height);
                Check(loaded.Air.V.SequenceEqual(w.Air.V), "saved plume velocity was not retained");
                double mass = loaded.Statistics().Mass, budget = Budget(loaded);
                for (int n = 0; n < 12; n++) loaded.Step();
                Check(loaded.Air.V.Min() < -.001, "restored steam no longer drove air");
                Near(loaded.Statistics().Mass + loaded.EscapedMass, mass, 1e-12); Near(Budget(loaded), budget, 1e-9);
                loaded.Clear(); loaded.Step();
                Check(!loaded.Air.Active && loaded.Air.V.All(v => v == 0), "cleared vapor left phantom buoyancy");
            }
            finally { File.Delete(path); }
        });
    }

    private static World SteamCloud(double amount = 1)
    {
        var w = new World(64, 64);
        PutSteamCloud(w, 28, 40, amount);
        return w;
    }

    private static void PutSteamCloud(World w, int x0, int y0, double amount)
    {
        for (int y = y0; y < y0 + 8; y++) for (int x = x0; x < x0 + 8; x++)
            w.At(x, y) = SurfaceVapor(20, Materials.Get(Element.Steam).Density * World.CellVolume * amount);
    }

    private static double SteamCenterY(World w)
    {
        double mass = 0, moment = 0;
        for (int i = 0; i < w.Cells.Length; i++) if (w.Cells[i].Element == Element.Steam)
        { mass += w.Cells[i].Mass; moment += w.Cells[i].Mass * (i / w.Width); }
        return moment / Math.Max(1e-30, mass);
    }

    // Empty rectangular air grid: integrate staggered dual-face masses, including
    // half volumes on open edges. This tests drag before pressure/wall impulses.
    private static double AirFaceMoment(AirField air, bool vertical, bool energy)
    {
        double result = 0, dx = air.Dx, dy = air.Dy;
        for (int y = 0; y < air.Height + (vertical ? 1 : 0); y++)
        for (int x = 0; x < air.Width + (vertical ? 0 : 1); x++)
        {
            bool edge = vertical ? y == 0 || y == air.Height : x == 0 || x == air.Width;
            double mass = AirField.Density * dx * dy * World.Depth * (edge ? .5 : 1);
            double v = air.NormalVelocityAtFineFace(x * AirField.Scale, y * AirField.Scale, vertical);
            result += energy ? .5 * mass * v * v : mass * v;
        }
        return result;
    }

    private static double CoupledMomentum(World w, bool vertical)
        => AirFaceMoment(w.Air, vertical, false) + w.Cells.Sum(p => p.Mass
            * (vertical ? p.VelocityY : p.VelocityX) * World.CellSize);

    private static double CoupledKineticEnergy(World w)
        => AirFaceMoment(w.Air, false, true) + AirFaceMoment(w.Air, true, true)
            + w.Cells.Sum(p => .5 * p.Mass * (p.VelocityX * p.VelocityX + p.VelocityY * p.VelocityY)
                * World.CellSize * World.CellSize);
}
