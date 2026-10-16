namespace Powder;

public sealed partial class LiquidField
{
    // Boussinesq pressure (kinematic units m²/s²). Gravity's hydrostatic part has
    // been subtracted; temperature / salinity produce the remaining buoyancy.
    private void Project(double seconds, double[] pressure)
    {
        equation.Count = 0; equation.Stride = Width;
        double a = 1 / (Dx * Dx);
        for (int i = 0; i < kind.Length; i++)
        {
            if (kind[i] == 0) continue;
            equation.Active[equation.Count++] = i;
            int x = i % Width, y = i / Width;
            double w = x > 0 && Same(i, i - 1) ? a : 0;
            double e = x + 1 < Width && Same(i, i + 1) ? a : 0;
            double n = y > 0 && Same(i, i - Width) ? a : 0;
            double s = y + 1 < Height && Same(i, i + Width) ? a : 0;
            equation.West[i] = w; equation.East[i] = e; equation.North[i] = n; equation.South[i] = s;
            equation.Diagonal[i] = Math.Max(1, w + e + n + s);
            int f = y * (Width + 1) + x;
            equation.Rhs[i] = -(U[f + 1] - U[f] + V[i + Width] - V[i]) / (Dx * seconds);
        }
        equation.Solve(pressure, 1e-5 / seconds);
        for (int y = 0; y < Height; y++)
        for (int x = 1; x < Width; x++)
        {
            int f = y * (Width + 1) + x, i = y * Width + x;
            if (tagU[f] != 0) U[f] -= seconds * (pressure[i] - pressure[i - 1]) / Dx;
        }
        for (int y = 1; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            if (tagV[i] != 0) V[i] -= seconds * (pressure[i] - pressure[i - Width]) / Dx;
        }
        double norm = 0; int count = 0;
        for (int i = 0; i < kind.Length; i++)
        {
            if (kind[i] == 0) continue;
            int f = i / Width * (Width + 1) + i % Width;
            double divergence = (U[f + 1] - U[f] + V[i + Width] - V[i]) / Dx;
            norm += divergence * divergence; count++;
        }
        DivergenceRms = count == 0 ? 0 : Math.Sqrt(norm / count);
    }
}
