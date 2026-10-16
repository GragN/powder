namespace Powder;

public static partial class SelfTests
{
    private static void RunLiquidSolverTests()
    {
        Test("a sleeping liquid wakes on heating and restored circulation", () =>
        {
            var w = LiquidTank(36, 40, Element.Water, 20);
            for (int n = 0; n < 4; n++) w.Liquids.Step(w, World.FixedDelta);
            Check(w.Liquids.U.Concat(w.Liquids.V).All(v => v == 0), "resting liquid moved");
            Scenes.Rect(w, 14, 29, 8, 6, Element.Water, 80);
            double energy = w.Statistics().Energy;
            for (int n = 0; n < 8; n++) w.Liquids.Step(w, World.FixedDelta);
            Check(w.Liquids.VelocityAtFine(18, 31).Y < -.001, "heating did not wake convection");
            Near(w.Statistics().Energy, energy, 1e-8);
            var state = w.Liquids.Capture();
            var still = LiquidTank(36, 40, Element.Water, 20);
            still.Liquids.Restore(still, state);
            still.Liquids.Step(still, World.FixedDelta);
            Check(still.Liquids.U.Concat(still.Liquids.V).Any(v => Math.Abs(v) > 1e-5), "rest detection discarded restored momentum");
        });
        Test("liquid pressure cache rebuilds after a vessel is divided", () =>
        {
            var w = LiquidTank(48, 40, Element.Water, 20);
            Scenes.Rect(w, 8, 29, 8, 6, Element.Water, 80);
            for (int n = 0; n < 5; n++) w.Liquids.Step(w, World.FixedDelta);
            Scenes.Rect(w, 24, 1, 1, 38, Element.Glass, 20);
            double mass = w.Statistics().Mass, energy = w.Statistics().Energy;
            for (int n = 0; n < 20; n++) w.Liquids.Step(w, World.FixedDelta);
            for (int y = 1; y < 39; y++)
            {
                Near(w.Liquids.U[y * 49 + 24], 0, 1e-12);
                Near(w.Liquids.U[y * 49 + 25], 0, 1e-12);
            }
            Check(w.Liquids.DivergenceRms < .001, "changed pressure domain did not converge");
            Near(w.Statistics().Mass, mass, 1e-12); Near(w.Statistics().Energy, energy, 1e-8);
        });
        Test("a collapsed Neumann pressure mode does not amplify rounding into a jet", () =>
        {
            const int width = 48, height = 32;
            var a = new GridEquation(width * height) { Stride = width, UseMultigrid = true };
            bool Wet(int x, int y) => x >= 1 && x <= 30 && y >= 1 && y <= 29
                || x == 42 && (y == 12 || y == 13);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                if (!Wet(x, y)) continue;
                int i = y * width + x;
                a.Active[a.Count++] = i;
                a.West[i] = Wet(x - 1, y) ? 1 : 0; a.East[i] = Wet(x + 1, y) ? 1 : 0;
                a.North[i] = Wet(x, y - 1) ? 1 : 0; a.South[i] = Wet(x, y + 1) ? 1 : 0;
                a.Diagonal[i] = a.West[i] + a.East[i] + a.North[i] + a.South[i];
            }
            // The tiny compatibility error is below the requested accuracy. It
            // must remain tiny instead of becoming an enormous constant pressure.
            int first = 12 * width + 42, second = 13 * width + 42;
            a.Rhs[first] = 1; a.Rhs[second] = -1 + 1e-13;
            var pressure = new double[width * height];
            a.Solve(pressure, 1e-8);
            Check(pressure.All(p => double.IsFinite(p) && Math.Abs(p) < 10), "pressure gauge amplified rounding");
            Near(pressure[first] - pressure[second], 1, 1e-7);
        });
    }
}
