namespace Powder;

public sealed partial class AirField
{
    // Matrix-free, diagonally preconditioned conjugate gradient. Open edges use
    // outside pressure p=0; solid faces and the bottom use no-normal-flow conditions.
    private void Project(double seconds)
    {
        double ax = 1 / (Dx * Dx), ay = 1 / (Dy * Dy);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            left[i] = right[i] = above[i] = below[i] = -1;
            if (Blocked[i]) { diagonal[i] = 1; pressure[i] = rhs[i] = 0; continue; }
            if (Fluid(x - 1, y)) left[i] = i - 1;
            if (Fluid(x + 1, y)) right[i] = i + 1;
            if (Fluid(x, y - 1)) above[i] = i - Width;
            if (Fluid(x, y + 1)) below[i] = i + Width;
            diagonal[i] = (x == 0 ? 2 * ax : Fluid(x - 1, y) ? ax : 0)
                + (x == Width - 1 ? 2 * ax : Fluid(x + 1, y) ? ax : 0)
                + (y == 0 ? 2 * ay : Fluid(x, y - 1) ? ay : 0)
                + (y + 1 < Height && Fluid(x, y + 1) ? ay : 0);
            if (diagonal[i] == 0) diagonal[i] = 1;
            rhs[i] = -((U[y * (Width + 1) + x + 1] - U[y * (Width + 1) + x]) / Dx
                + (V[(y + 1) * Width + x] - V[y * Width + x]) / Dy) / seconds;
        }
        Multiply(pressure, product, ax, ay);
        double rz = 0;
        for (int i = 0; i < Heat.Length; i++)
        {
            residual[i] = rhs[i] - product[i];
            direction[i] = residual[i] / diagonal[i];
            rz += residual[i] * direction[i];
        }
        for (int iteration = 0; iteration < 160 && rz > 1e-16; iteration++)
        {
            Multiply(direction, product, ax, ay);
            double denominator = 0;
            for (int i = 0; i < Heat.Length; i++) denominator += direction[i] * product[i];
            if (denominator <= 1e-25) break;
            double alpha = rz / denominator, nextRz = 0, norm = 0;
            for (int i = 0; i < Heat.Length; i++)
            {
                pressure[i] += alpha * direction[i]; residual[i] -= alpha * product[i];
                nextRz += residual[i] * residual[i] / diagonal[i]; norm += residual[i] * residual[i];
            }
            if (norm / Heat.Length < 1e-8 / (seconds * seconds)) break;
            double beta = nextRz / rz;
            for (int i = 0; i < Heat.Length; i++) direction[i] = residual[i] / diagonal[i] + beta * direction[i];
            rz = nextRz;
        }
        for (int y = 0; y < Height; y++)
        for (int x = 0; x <= Width; x++)
        {
            int i = y * (Width + 1) + x;
            if (!OpenU(x, y)) { U[i] = 0; continue; }
            double left = x == 0 ? 0 : pressure[y * Width + x - 1];
            double right = x == Width ? 0 : pressure[y * Width + x];
            U[i] -= seconds * (right - left) / Dx * (x == 0 || x == Width ? 2 : 1);
        }
        for (int y = 0; y <= Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            if (!OpenV(x, y)) { V[i] = 0; continue; }
            double above = y == 0 ? 0 : pressure[(y - 1) * Width + x];
            V[i] -= seconds * (pressure[y * Width + x] - above) / Dy * (y == 0 ? 2 : 1);
        }
        double sum = 0; int count = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Fluid(x, y)) continue;
            double div = (U[y * (Width + 1) + x + 1] - U[y * (Width + 1) + x]) / Dx
                + (V[(y + 1) * Width + x] - V[y * Width + x]) / Dy;
            sum += div * div; count++;
        }
        DivergenceRms = count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private void Multiply(double[] source, double[] target, double ax, double ay)
    {
        for (int i = 0; i < source.Length; i++)
        {
            double value = diagonal[i] * source[i];
            if (left[i] >= 0) value -= ax * source[left[i]];
            if (right[i] >= 0) value -= ax * source[right[i]];
            if (above[i] >= 0) value -= ay * source[above[i]];
            if (below[i] >= 0) value -= ay * source[below[i]];
            target[i] = value;
        }
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
    }
}
