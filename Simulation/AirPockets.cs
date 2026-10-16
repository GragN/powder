namespace Powder;

public sealed partial class AirField
{
    // The velocity grid conservatively closes a whole block at a thin wall. Its
    // remaining gas still exists: resolve its heat on the particle grid instead
    // of showing an ambient-temperature hole or reaching past it for contact.
    public double[] PocketHeat { get; }
    public const double PocketCapacity = Density * SpecificHeat * World.CellVolume;
    private readonly bool[] pocketAir;
    private List<int> pocketIndices = [], nextPocketIndices = [];
    private bool trappedPocketHeat, reversePocketSweep;
    private struct AirContact
    {
        public int From, To;
        public byte Layer; // fine air, coarse air, outside atmosphere
        public double Ratio, Rate, Factor;
    }
    private readonly List<AirContact> pocketContacts = [];
    private double pocketSeconds = -1;

    private static bool Condensed(Element element) => Materials.Get(element).State
        is MatterState.Solid or MatterState.Powder or MatterState.Liquid;

    private void UpdatePocketGeometry(World world, bool coarseChanged)
    {
        if (obstacleCount == 0 && pocketIndices.Count == 0 && !trappedPocketHeat) return;
        nextPocketIndices.Clear();
        bool changed = false, unresolvedCoarseHeat = false;
        for (int cy = 0; cy < Height; cy++)
        for (int cx = 0; cx < Width; cx++)
        {
            int coarse = cy * Width + cx;
            if (!nextBlocked[coarse]) continue;
            unresolvedCoarseHeat |= Heat[coarse] != 0;
            for (int y = fineRows[cy]; y < fineRows[cy + 1]; y++)
            for (int x = fineColumns[cx]; x < fineColumns[cx + 1]; x++)
            {
                int i = y * fineWidth + x;
                if (Condensed(world.Cells[i].Element)) continue;
                int k = nextPocketIndices.Count;
                changed |= k >= pocketIndices.Count || pocketIndices[k] != i;
                nextPocketIndices.Add(i);
            }
        }
        changed |= nextPocketIndices.Count != pocketIndices.Count;
        if (!changed && !coarseChanged && !unresolvedCoarseHeat && !trappedPocketHeat) return;
        if (changed)
        {
            (pocketIndices, nextPocketIndices) = (nextPocketIndices, pocketIndices);
            Array.Clear(pocketAir);
            foreach (int i in pocketIndices) pocketAir[i] = true;
        }
        if (changed || coarseChanged) RebuildPocketContacts();

        // Moving material can cover a fine gas cell or reopen its coarse block.
        // Move its extensive energy exactly once; never reset its temperature.
        trappedPocketHeat = false;
        for (int i = 0; i < PocketHeat.Length; i++)
        {
            if (PocketHeat[i] == 0 || pocketAir[i]) continue;
            int x = i % fineWidth, y = i / fineWidth, coarse = IndexAtFine(x, y);
            if (!nextBlocked[coarse]) { Heat[coarse] += PocketHeat[i]; PocketHeat[i] = 0; continue; }
            int count = 0;
            for (int k = 0; k < 4; k++)
            {
                int xx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), yy = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                if (xx >= 0 && yy >= 0 && xx < fineWidth && yy < fineHeight
                    && !Condensed(world.At(xx, yy).Element)) count++;
            }
            if (count == 0) { trappedPocketHeat = true; continue; }
            double q = PocketHeat[i] / count; PocketHeat[i] = 0;
            for (int k = 0; k < 4; k++)
            {
                int xx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), yy = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                if (xx < 0 || yy < 0 || xx >= fineWidth || yy >= fineHeight || Condensed(world.At(xx, yy).Element)) continue;
                int next = yy * fineWidth + xx;
                if (pocketAir[next]) PocketHeat[next] += q;
                else Heat[IndexAtFine(xx, yy)] += q;
            }
        }

        for (int cy = 0; cy < Height; cy++)
        for (int cx = 0; cx < Width; cx++)
        {
            int coarse = cy * Width + cx;
            if (!nextBlocked[coarse] || Heat[coarse] == 0) continue;
            int count = 0;
            for (int y = fineRows[cy]; y < fineRows[cy + 1]; y++)
            for (int x = fineColumns[cx]; x < fineColumns[cx + 1]; x++)
                if (pocketAir[y * fineWidth + x]) count++;
            if (count > 0)
            {
                double q = Heat[coarse] / count; Heat[coarse] = 0;
                for (int y = fineRows[cy]; y < fineRows[cy + 1]; y++)
                for (int x = fineColumns[cx]; x < fineColumns[cx + 1]; x++)
                    if (pocketAir[y * fineWidth + x]) PocketHeat[y * fineWidth + x] += q;
                continue;
            }
            // A completely filled block has no remaining air. Displace its heat
            // into neighbouring open blocks, retaining it if none are resolved.
            if (cx > 0 && !nextBlocked[coarse - 1]) count++;
            if (cx + 1 < Width && !nextBlocked[coarse + 1]) count++;
            if (cy > 0 && !nextBlocked[coarse - Width]) count++;
            if (cy + 1 < Height && !nextBlocked[coarse + Width]) count++;
            if (count == 0) continue;
            double displaced = Heat[coarse] / count; Heat[coarse] = 0;
            if (cx > 0 && !nextBlocked[coarse - 1]) Heat[coarse - 1] += displaced;
            if (cx + 1 < Width && !nextBlocked[coarse + 1]) Heat[coarse + 1] += displaced;
            if (cy > 0 && !nextBlocked[coarse - Width]) Heat[coarse - Width] += displaced;
            if (cy + 1 < Height && !nextBlocked[coarse + Width]) Heat[coarse + Width] += displaced;
        }
    }

    private bool PocketsActive()
    {
        foreach (int i in pocketIndices) if (Math.Abs(PocketHeat[i]) > 1e-14) return true;
        return false;
    }

    private void TransportPocketHeat(double seconds)
    {
        // Exact two-capacity exchanges stay bounded even in a one-cell gap.
        // The two half sweeps surround bulk transport; all interfaces subtract
        // exactly the energy added to the receiving finite air volume.
        var contacts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pocketContacts);
        if (pocketSeconds != seconds)
        {
            foreach (ref var face in contacts)
                face.Factor = (1 - Math.Exp(-face.Rate * seconds)) / (1 + face.Ratio);
            pocketSeconds = seconds;
        }
        reversePocketSweep = !reversePocketSweep;
        for (int k = 0; k < contacts.Length; k++)
        {
            ref readonly var face = ref contacts[reversePocketSweep ? contacts.Length - 1 - k : k];
            double other = face.Layer == 0 ? PocketHeat[face.To] : face.Layer == 1 ? Heat[face.To] : 0;
            double q = (PocketHeat[face.From] - face.Ratio * other) * face.Factor;
            PocketHeat[face.From] -= q;
            if (face.Layer == 0) PocketHeat[face.To] += q;
            else if (face.Layer == 1) Heat[face.To] += q;
            else BoundaryHeat += q;
        }
    }

    private void RebuildPocketContacts()
    {
        pocketContacts.Clear(); pocketSeconds = -1;
        foreach (int i in pocketIndices)
        {
            int x = i % fineWidth, y = i / fineWidth;
            PocketFace(i, x - 1, y); PocketFace(i, x + 1, y);
            PocketFace(i, x, y - 1); PocketFace(i, x, y + 1);
        }
    }

    private void AddPocketContact(int from, int to, byte layer, double g, double ratio)
        => pocketContacts.Add(new AirContact { From = from, To = to, Layer = layer,
            Ratio = ratio, Rate = g / PocketCapacity * (1 + ratio) });

    private void PocketFace(int from, int x, int y)
    {
        if (y >= fineHeight) return; // impermeable floor
        if (x < 0 || x >= fineWidth || y < 0)
        {
            AddPocketContact(from, -1, 2, 2 * Conductivity * World.Depth, 0);
            return;
        }
        int to = y * fineWidth + x, coarse = IndexAtFine(x, y);
        if (pocketAir[to])
        {
            if (to < from) return; // visit a fine/fine face only once
            AddPocketContact(from, to, 0, Conductivity * World.Depth, 1);
        }
        else if (!nextBlocked[coarse])
        {
            int fx = from % fineWidth, fy = from / fineWidth;
            double distance = fx != x ? Math.Abs((coarse % Width + .5) * Dx - (fx + .5) * World.CellSize)
                : Math.Abs((coarse / Width + .5) * Dy - (fy + .5) * World.CellSize);
            double g = Conductivity * World.Depth * World.CellSize / Math.Max(World.CellSize * .5, distance);
            AddPocketContact(from, coarse, 1, g, PocketCapacity / Capacity);
        }
    }
}
