using System.Runtime.InteropServices;

namespace Powder;

public sealed partial class AirField
{
    private int TransportSteps(double seconds, double diffusivity)
    {
        Array.Clear(delta, 0, cells.Count); Array.Clear(rhs, 0, cells.Count);
        foreach (var face in faces)
        {
            double q = face.Area * face.Velocity, diffusion = diffusivity * face.Area / face.Distance;
            if (face.A >= 0) { delta[face.A] += Math.Max(0, q) + diffusion; rhs[face.A] += Math.Max(0, -q) + diffusion; }
            if (face.B >= 0) { delta[face.B] += Math.Max(0, -q) + diffusion; rhs[face.B] += Math.Max(0, q) + diffusion; }
        }
        double rate = 0;
        for (int i = 0; i < cells.Count; i++) rate = Math.Max(rate, Math.Max(delta[i], rhs[i]) / sizes[i]);
        if (!double.IsFinite(rate) || rate * seconds > 100_000)
            throw new InvalidOperationException($"Unstable atmosphere time step: rate={rate:E3}/s, dt={seconds:G3}s, divergence={MaximumDivergence:E3}/s.");
        return Math.Max(1, (int)Math.Ceiling(seconds * rate / .5));
    }

    private void PredictMomentum(double seconds)
    {
        int steps = TransportSteps(seconds, Viscosity);
        double dt = seconds / steps;
        var nodes = CollectionsMarshal.AsSpan(cells);
        var fluxes = CollectionsMarshal.AsSpan(faces);
        for (int step = 0; step < steps; step++)
        {
            ReconstructVelocity(); Array.Clear(du, 0, nodes.Length); Array.Clear(dv, 0, nodes.Length);
            foreach (var face in fluxes)
            {
                double volumeFlux = face.Area * face.Velocity;
                int donor = volumeFlux >= 0 ? face.A : face.B;
                double fluxU = donor >= 0 ? volumeFlux * nodes[donor].U : 0;
                double fluxV = donor >= 0 ? volumeFlux * nodes[donor].V : 0;
                if (face.A >= 0 && face.B >= 0)
                {
                    double g = Viscosity * face.Area / face.Distance;
                    fluxU += g * (nodes[face.A].U - nodes[face.B].U);
                    fluxV += g * (nodes[face.A].V - nodes[face.B].V);
                }
                // Advective form equals the conservative momentum flux for a
                // divergence-free field. Its volume correction avoids treating
                // provisional pressure imbalance as real compression of air.
                if (face.A >= 0)
                { du[face.A] -= (fluxU - volumeFlux * nodes[face.A].U) * dt / sizes[face.A]; dv[face.A] -= (fluxV - volumeFlux * nodes[face.A].V) * dt / sizes[face.A]; }
                if (face.B >= 0)
                { du[face.B] += (fluxU - volumeFlux * nodes[face.B].U) * dt / sizes[face.B]; dv[face.B] += (fluxV - volumeFlux * nodes[face.B].V) * dt / sizes[face.B]; }
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                // Viscous wall drag is integrated exactly rather than imposing
                // a whole-block zero velocity around a particle-sized wall.
                double drag = Math.Exp(-Viscosity * nodes[i].WallArea / (sizes[i] * World.CellSize * .5) * dt) - 1;
                du[i] += nodes[i].U * drag; dv[i] += nodes[i].V * drag;
            }
            foreach (ref var face in fluxes)
            {
                double a = face.A >= 0 ? (face.Y ? nodes[face.A].V + dv[face.A] : nodes[face.A].U + du[face.A]) : 0;
                double b = face.B >= 0 ? (face.Y ? nodes[face.B].V + dv[face.B] : nodes[face.B].U + du[face.B]) : 0;
                face.Velocity = face.BlendA * a + (1 - face.BlendA) * b;
                if (face.Y)
                {
                    double ta = face.A >= 0 ? CellTemperature(face.A) : AmbientTemperature;
                    double tb = face.B >= 0 ? CellTemperature(face.B) : AmbientTemperature;
                    double t = face.BlendA * ta + (1 - face.BlendA) * tb;
                    face.Velocity += dt * World.Gravity * ((AmbientTemperature + 273.15) / Math.Max(50, t + 273.15) - 1);
                }
            }
        }
    }

    private void TransportHeat(double seconds)
    {
        int steps = TransportSteps(seconds, Conductivity / (Density * SpecificHeat));
        double dt = seconds / steps;
        var nodes = CollectionsMarshal.AsSpan(cells);
        for (int step = 0; step < steps; step++)
        {
            Array.Clear(delta, 0, nodes.Length);
            foreach (var face in faces)
            {
                double q = face.Area * face.Velocity;
                int donor = q >= 0 ? face.A : face.B;
                double energyFlux = donor >= 0 ? q * nodes[donor].Heat / sizes[donor] : 0;
                double ta = face.A >= 0 ? nodes[face.A].Heat / nodes[face.A].Capacity : 0;
                double tb = face.B >= 0 ? nodes[face.B].Heat / nodes[face.B].Capacity : 0;
                double transferred = dt * (energyFlux + Conductivity * face.Area / face.Distance * (ta - tb));
                if (face.A >= 0) delta[face.A] -= transferred; else BoundaryHeat -= transferred;
                if (face.B >= 0) delta[face.B] += transferred; else BoundaryHeat += transferred;
            }
            for (int i = 0; i < nodes.Length; i++) nodes[i].Heat += delta[i];
        }
    }
}
