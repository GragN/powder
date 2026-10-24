using System.Runtime.InteropServices;

namespace Powder;

public sealed partial class AirField
{
    // Equal-temperature density ratio at the same pressure: water vapor / dry air.
    private const double SteamMolarRatio = 18.01528 / 28.96546;
    private const double SteamMixingTime = .04;
    private double[] steamFraction = [], inverseFaceMass = [], steamInverseMassX = [], steamInverseMassY = [];
    private int[] steamRows = [], steamFaces = [], steamCursor = [];

    private static void GrowSteamArray<T>(ref T[] array, int count)
    {
        if (array.Length < count)
            Array.Resize(ref array, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, count)));
    }

    private void BuildSteamCoupling()
    {
        int count = cells.Count;
        GrowSteamArray(ref steamFraction, count);
        GrowSteamArray(ref steamRows, count + 1); GrowSteamArray(ref steamCursor, count);
        GrowSteamArray(ref steamFaces, faces.Count * 2); GrowSteamArray(ref inverseFaceMass, faces.Count);
        GrowSteamArray(ref steamInverseMassX, count); GrowSteamArray(ref steamInverseMassY, count);
        Array.Clear(steamRows, 0, count + 1);
        Array.Clear(steamInverseMassX, 0, count); Array.Clear(steamInverseMassY, 0, count);
        for (int f = 0; f < faces.Count; f++)
        {
            var face = faces[f];
            // Dual face volume = area * distance between pressure locations.
            double inverse = inverseFaceMass[f] = face.InverseDistance / (Density * face.Area);
            var weights = face.Y ? weightY : weightX;
            var inverseMass = face.Y ? steamInverseMassY : steamInverseMassX;
            for (int side = 0; side < 2; side++)
            {
                int i = side == 0 ? face.A : face.B;
                if (i < 0) continue;
                steamRows[i + 1]++;
                double w = face.Area / weights[i];
                inverseMass[i] += w * w * inverse;
            }
        }
        for (int i = 0; i < count; i++) steamRows[i + 1] += steamRows[i];
        Array.Copy(steamRows, steamCursor, count);
        for (int f = 0; f < faces.Count; f++)
        {
            if (faces[f].A >= 0) steamFaces[steamCursor[faces[f].A]++] = f;
            if (faces[f].B >= 0) steamFaces[steamCursor[faces[f].B]++] = f;
        }
    }

    private bool UpdateSteamContent(World? world)
    {
        Array.Clear(steamFraction, 0, cells.Count);
        if (world is null) return false;
        bool any = false;
        for (int i = 0; i < world.Cells.Length; i++)
            if (world.Cells[i].Element == Element.Steam && cellAtFine[i] >= 0)
            { steamFraction[cellAtFine[i]] += world.Cells[i].Mass; any = true; }
        foreach (int i in world.BubbleLocations)
            if (world.Bubbles[i].Element == Element.Steam && cellAtFine[i] >= 0)
            { steamFraction[cellAtFine[i]] += world.Bubbles[i].Mass; any = true; }
        if (!any) return false;
        for (int i = 0; i < cells.Count; i++)
        {
            double vaporDensity = Density * SteamMolarRatio * (AmbientTemperature + 273.15)
                / Math.Max(50, CellTemperature(i) + 273.15);
            // Unresolved boiling parcels can exceed one control volume. They
            // cannot make its vapor fraction exceed pure steam or reverse density.
            steamFraction[i] = Math.Min(1, steamFraction[i] / (vaporDensity * cells[i].Volume));
        }
        return true;
    }

    internal void CoupleSteamMomentum(World world, double seconds)
    {
        if (seconds <= 0) return;
        for (int i = 0; i < world.Cells.Length; i++)
            if (world.Cells[i].Element == Element.Steam && cellAtFine[i] >= 0)
                RelaxSteam(ref world.Cells[i], cellAtFine[i], seconds);
        foreach (int i in world.BubbleLocations)
            if (world.Bubbles[i].Element == Element.Steam && cellAtFine[i] >= 0)
                RelaxSteam(ref world.Bubbles[i], cellAtFine[i], seconds);
    }

    private void RelaxSteam(ref Particle parcel, int node, double seconds)
    {
        RelaxSteamAxis(ref parcel.VelocityX, parcel.Mass, node, false, seconds);
        RelaxSteamAxis(ref parcel.VelocityY, parcel.Mass, node, true, seconds);
    }

    private void RelaxSteamAxis(ref double velocity, double mass, int node, bool vertical, double seconds)
    {
        double weightSum = vertical ? weightY[node] : weightX[node];
        double inverseAirMass = vertical ? steamInverseMassY[node] : steamInverseMassX[node];
        if (weightSum <= 0 || mass <= 0) return;
        var fluxes = CollectionsMarshal.AsSpan(faces);
        double airVelocity = 0;
        for (int k = steamRows[node]; k < steamRows[node + 1]; k++)
        {
            ref readonly var face = ref fluxes[steamFaces[k]];
            if (face.Y == vertical) airVelocity += face.Area / weightSum * face.Velocity;
        }
        // Exact, finite-mass drag relaxation. Interpolation and force deposition
        // use the same weights: reaction equals action, even for a heavy parcel.
        double reducedMass = 1 / (1 / mass + inverseAirMass);
        double decay = Math.Exp(-seconds / SteamMixingTime * (1 + mass * inverseAirMass));
        double impulse = reducedMass * (velocity * World.CellSize - airVelocity) * (1 - decay);
        velocity -= impulse / (mass * World.CellSize);
        for (int k = steamRows[node]; k < steamRows[node + 1]; k++)
        {
            int f = steamFaces[k];
            if (fluxes[f].Y == vertical)
                fluxes[f].Velocity += fluxes[f].Area / weightSum * impulse * inverseFaceMass[f];
        }
    }
}
