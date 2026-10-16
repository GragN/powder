namespace Powder;

public sealed partial class AirField
{
    // The pressure stencil changes only when the obstacle mask changes. Reuse the
    // same incomplete-Cholesky-preconditioned solver as the liquid MAC grid.
    private void RebuildPressureGeometry()
    {
        var a = pressureEquation;
        double ax = 1 / (Dx * Dx), ay = 1 / (Dy * Dy);
        a.Count = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            a.West[i] = a.East[i] = a.North[i] = a.South[i] = 0;
            if (Blocked[i]) { a.Diagonal[i] = 1; pressure[i] = 0; continue; }
            if (x > 0 && !Blocked[i - 1]) a.West[i] = ax;
            if (x + 1 < Width && !Blocked[i + 1]) a.East[i] = ax;
            if (y > 0 && !Blocked[i - Width]) a.North[i] = ay;
            if (y + 1 < Height && !Blocked[i + Width]) a.South[i] = ay;
            a.Diagonal[i] = a.West[i] + a.East[i] + a.North[i] + a.South[i]
                + (x == 0 || x == Width - 1 ? 2 * ax : 0) + (y == 0 ? 2 * ay : 0);
            if (a.Diagonal[i] == 0) a.Diagonal[i] = 1;
            a.Active[a.Count++] = i;
        }
        a.MatrixChanged();
    }

    // Open edges use outside pressure p=0; solid faces and the bottom use
    // no-normal-flow conditions. The accuracy target is the original 1e-4 / s.
    private void Project(double seconds)
    {
        var a = pressureEquation;
        for (int k = 0; k < a.Count; k++)
        {
            int i = a.Active[k], x = i % Width, y = i / Width;
            a.Rhs[i] = -((U[y * (Width + 1) + x + 1] - U[y * (Width + 1) + x]) / Dx
                + (V[i + Width] - V[i]) / Dy) / seconds;
        }
        a.Solve(pressure, 1e-4 / seconds, 160);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x <= Width; x++)
        {
            int i = y * (Width + 1) + x;
            if (!openU[i]) { U[i] = 0; continue; }
            double left = x == 0 ? 0 : pressure[y * Width + x - 1];
            double right = x == Width ? 0 : pressure[y * Width + x];
            U[i] -= seconds * (right - left) / Dx * (x == 0 || x == Width ? 2 : 1);
        }
        for (int y = 0; y <= Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            if (!openV[i]) { V[i] = 0; continue; }
            double above = y == 0 ? 0 : pressure[i - Width];
            V[i] -= seconds * (pressure[i] - above) / Dy * (y == 0 ? 2 : 1);
        }
        double sum = 0;
        for (int k = 0; k < a.Count; k++)
        {
            int i = a.Active[k], x = i % Width, y = i / Width;
            double div = (U[y * (Width + 1) + x + 1] - U[y * (Width + 1) + x]) / Dx
                + (V[i + Width] - V[i]) / Dy;
            sum += div * div;
        }
        DivergenceRms = a.Count == 0 ? 0 : Math.Sqrt(sum / a.Count);
    }

    public sealed record State(int Width, int Height, double AmbientTemperature,
        double[] Heat, double[] U, double[] V, double BoundaryHeat);
    public State Capture() => new(Width, Height, AmbientTemperature, (double[])Heat.Clone(),
        (double[])U.Clone(), (double[])V.Clone(), BoundaryHeat);
    public void Restore(State state)
    {
        if (state.Width != Width || state.Height != Height || !double.IsFinite(state.AmbientTemperature)
            || state.AmbientTemperature < -273.15 || state.Heat is null || state.U is null || state.V is null
            || state.Heat.Length != Heat.Length || state.U.Length != U.Length || state.V.Length != V.Length
            || state.Heat.Any(h => !double.IsFinite(h) || Math.Abs(h) > Capacity * 100_000)
            || state.U.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100)
            || state.V.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100) || !double.IsFinite(state.BoundaryHeat))
            throw new InvalidDataException("Invalid atmosphere data.");
        AmbientTemperature = state.AmbientTemperature; BoundaryHeat = state.BoundaryHeat;
        state.Heat.CopyTo(Heat, 0); state.U.CopyTo(U, 0); state.V.CopyTo(V, 0);
        Array.Clear(pressure);
        ZeroClosedFaces();
    }
}
