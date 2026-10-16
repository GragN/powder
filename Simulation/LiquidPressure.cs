namespace Powder;

public sealed partial class LiquidField
{
    // Boussinesq pressure (kinematic units m²/s²). Gravity's hydrostatic part has
    // been subtracted; temperature / salinity produce the remaining buoyancy.
    private void Project(double seconds, double[] pressure)
    {
        var equation = pressureEquation;
        double a = 1 / (Dx * Dx);
        if (geometryChanged)
        {
            equation.Count = activeCount; equation.Stride = Width;
            for (int k = 0; k < activeCount; k++)
            {
                int i = active[k], x = i % Width, y = i / Width;
                equation.Active[k] = i;
                double w = x > 0 && Same(i, i - 1) ? a : 0;
                double e = x + 1 < Width && Same(i, i + 1) ? a : 0;
                double n = y > 0 && Same(i, i - Width) ? a : 0;
                double s = y + 1 < Height && Same(i, i + Width) ? a : 0;
                equation.West[i] = w; equation.East[i] = e; equation.North[i] = n; equation.South[i] = s;
                equation.Diagonal[i] = Math.Max(1, w + e + n + s);
            }
            equation.MatrixChanged(); geometryChanged = false;
        }
        for (int k = 0; k < activeCount; k++)
        {
            int i = active[k], x = i % Width, y = i / Width;
            int f = y * (Width + 1) + x;
            equation.Rhs[i] = -(U[f + 1] - U[f] + V[i + Width] - V[i]) / (Dx * seconds);
        }
        equation.Solve(pressure, 1e-5 / seconds);
        for (int k = 0; k < faceCountU; k++)
        {
            int f = facesU[k], i = f - f / (Width + 1);
            U[f] -= seconds * (pressure[i] - pressure[i - 1]) / Dx;
        }
        for (int k = 0; k < faceCountV; k++)
        {
            int i = facesV[k];
            V[i] -= seconds * (pressure[i] - pressure[i - Width]) / Dx;
        }
        double norm = 0; int count = 0;
        for (int k = 0; k < activeCount; k++)
        {
            int i = active[k];
            int f = i / Width * (Width + 1) + i % Width;
            double divergence = (U[f + 1] - U[f] + V[i + Width] - V[i]) / Dx;
            norm += divergence * divergence; count++;
        }
        DivergenceRms = count == 0 ? 0 : Math.Sqrt(norm / count);
    }
}
