using System.Runtime.InteropServices;

namespace Powder;

public sealed partial class AirField
{
    private void Project(double seconds)
    {
        Array.Clear(rhs, 0, cells.Count);
        foreach (var face in faces)
        {
            double flux = face.Area * face.Velocity;
            if (face.A >= 0) rhs[face.A] -= flux / seconds;
            if (face.B >= 0) rhs[face.B] += flux / seconds;
        }
        pressureSolver.Solve(rhs, pressure, sizes, 1e-4, seconds);
        foreach (ref var face in CollectionsMarshal.AsSpan(faces))
        {
            double a = face.A >= 0 ? pressure[face.A] : 0, b = face.B >= 0 ? pressure[face.B] : 0;
            face.Velocity -= seconds * (b - a) * face.InverseDistance;
        }
        Array.Clear(rhs, 0, cells.Count);
        foreach (var face in faces)
        {
            double flux = face.Area * face.Velocity;
            if (face.A >= 0) rhs[face.A] += flux;
            if (face.B >= 0) rhs[face.B] -= flux;
        }
        double norm = 0; MaximumDivergence = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            double div = rhs[i] / sizes[i];
            norm += div * div * sizes[i]; MaximumDivergence = Math.Max(MaximumDivergence, Math.Abs(div));
        }
        DivergenceRms = TotalAirVolume > 0 ? Math.Sqrt(norm / TotalAirVolume) : 0;
    }
}
