using System.Runtime.InteropServices;

namespace Powder;

public sealed partial class AirField
{
    public sealed record State(int Width, int Height, double AmbientTemperature,
        double[] Heat, double[] U, double[] V, double BoundaryHeat, double[]? PocketHeat = null,
        double[]? VolumeHeat = null, double[]? FaceVelocity = null, double[]? TrappedHeat = null);

    public State Capture() => new(Width, Height, AmbientTemperature, (double[])Heat.Clone(),
        (double[])U.Clone(), (double[])V.Clone(), BoundaryHeat, null,
        cells.Select(c => c.Heat).ToArray(), faces.Select(f => f.Velocity).ToArray(), (double[])trappedHeat.Clone());

    public void Restore(State state)
    {
        if (state.Width != Width || state.Height != Height || !double.IsFinite(state.AmbientTemperature)
            || state.AmbientTemperature < -273.15 || state.Heat is null || state.U is null || state.V is null
            || state.Heat.Length != Heat.Length || state.U.Length != U.Length || state.V.Length != V.Length
            || state.Heat.Any(h => !double.IsFinite(h) || Math.Abs(h) > Capacity * 100_000)
            || state.U.Concat(state.V).Any(v => !double.IsFinite(v) || Math.Abs(v) > 100)
            || !double.IsFinite(state.BoundaryHeat)
            || state.PocketHeat is not null && (state.PocketHeat.Length != trappedHeat.Length
                || state.PocketHeat.Any(h => !double.IsFinite(h) || Math.Abs(h) > Density * SpecificHeat * World.CellVolume * 100_000)))
            throw new InvalidDataException("Invalid atmosphere data.");
        AmbientTemperature = state.AmbientTemperature; BoundaryHeat = state.BoundaryHeat;
        var nodes = CollectionsMarshal.AsSpan(cells); var fluxes = CollectionsMarshal.AsSpan(faces);
        foreach (ref var node in nodes) node.Heat = 0;
        Array.Clear(trappedHeat); Array.Clear(pressure);
        if (state.VolumeHeat is not null || state.FaceVelocity is not null || state.TrappedHeat is not null)
        {
            if (state.VolumeHeat is null || state.FaceVelocity is null || state.TrappedHeat is null
                || state.VolumeHeat.Length != cells.Count || state.FaceVelocity.Length != faces.Count
                || state.TrappedHeat.Length != trappedHeat.Length
                || state.FaceVelocity.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100)
                || state.TrappedHeat.Any(h => !double.IsFinite(h) || Math.Abs(h) > Capacity * 100_000))
                throw new InvalidDataException("Invalid finite-volume atmosphere data.");
            for (int i = 0; i < nodes.Length; i++)
            {
                double h = state.VolumeHeat[i];
                if (!double.IsFinite(h) || Math.Abs(h) > nodes[i].Capacity * 100_000)
                    throw new InvalidDataException("Invalid finite-volume thermal energy.");
                nodes[i].Heat = h;
            }
            for (int i = 0; i < fluxes.Length; i++) fluxes[i].Velocity = state.FaceVelocity[i];
            state.TrappedHeat.CopyTo(trappedHeat, 0);
        }
        else
        {
            // Versions 1..6 used evenly mapped coarse blocks, plus optional fine
            // pockets. Remap every joule by overlap, including irregular sizes.
            int[] oldCounts = new int[Heat.Length];
            for (int y = 0; y < fineHeight; y++) for (int x = 0; x < fineWidth; x++)
                oldCounts[y * Height / fineHeight * Width + x * Width / fineWidth]++;
            for (int y = 0; y < fineHeight; y++) for (int x = 0; x < fineWidth; x++)
            {
                int fine = y * fineWidth + x, old = y * Height / fineHeight * Width + x * Width / fineWidth;
                double q = state.Heat[old] / oldCounts[old] + (state.PocketHeat?[fine] ?? 0);
                int i = cellAtFine[fine];
                if (i >= 0) nodes[i].Heat += q; else trappedHeat[fine] += q;
            }
            // Opening a formerly excluded region starts it at ambient temperature;
            // already stored heat is moved only through touching gas faces.
            for (int i = 0; i < trappedHeat.Length; i++)
            {
                if (trappedHeat[i] == 0) continue;
                int x = i % fineWidth, y = i / fineWidth, count = 0;
                for (int k = 0; k < 4; k++) if (VolumeIndexAtFine(x + (k == 0 ? -1 : k == 1 ? 1 : 0),
                    y + (k == 2 ? -1 : k == 3 ? 1 : 0)) >= 0) count++;
                if (count == 0) continue;
                double q = trappedHeat[i] / count; trappedHeat[i] = 0;
                for (int k = 0; k < 4; k++)
                {
                    int next = VolumeIndexAtFine(x + (k == 0 ? -1 : k == 1 ? 1 : 0), y + (k == 2 ? -1 : k == 3 ? 1 : 0));
                    if (next >= 0) nodes[next].Heat += q;
                }
            }
            state.U.CopyTo(U, 0); state.V.CopyTo(V, 0); ImportVelocityEdits();
        }
        ReconstructVelocity(); PublishViews();
    }
}
