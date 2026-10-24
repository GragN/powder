using System.Runtime.InteropServices;

namespace Powder;

public sealed partial class AirField
{
    private readonly List<PressureLink> pressureLinks = [];
    private readonly List<PressurePoint> pressurePoints = [];
    private readonly List<int> faceOrder = [];

    public void UpdateObstacles(World world)
    {
        ImportVelocityEdits();
        bool changed = false;
        for (int i = 0; i < solid.Length; i++)
        {
            Element e = world.Cells[i].Element;
            bool occupied = e != Element.Empty && Materials.Get(e).State
                is MatterState.Solid or MatterState.Powder or MatterState.Liquid
                && !World.SubcellDrop(world.Cells[i]);
            nextSolid[i] = occupied; changed |= occupied != solid[i];
        }
        if (!changed) return;
        (solid, nextSolid) = (nextSolid, solid);
        RebuildGeometry();
    }

    private void RebuildGeometry()
    {
        nextCells.Clear(); Array.Fill(nextCellAtFine, -1);
        for (int by = 0; by < Height; by++) for (int bx = 0; bx < Width; bx++)
        {
            int x0 = bx * Scale, y0 = by * Scale;
            int w = Math.Min(Scale, fineWidth - x0), h = Math.Min(Scale, fineHeight - y0);
            bool empty = true;
            for (int y = y0; y < y0 + h && empty; y++) for (int x = x0; x < x0 + w; x++)
                if (solid[y * fineWidth + x]) { empty = false; break; }
            if (empty) AddVolume(x0, y0, w, h, by * Width + bx);
            else for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++)
                if (!solid[y * fineWidth + x]) AddVolume(x, y, 1, 1, by * Width + bx);
        }

        var oldNodes = CollectionsMarshal.AsSpan(cells);
        var newNodes = CollectionsMarshal.AsSpan(nextCells);
        for (int i = 0; i < cellAtFine.Length; i++)
        {
            int old = cellAtFine[i], next = nextCellAtFine[i];
            double q = old >= 0 ? oldNodes[old].Heat / oldNodes[old].Pixels : 0;
            if (next >= 0)
            {
                ref var node = ref newNodes[next];
                node.Heat += q + trappedHeat[i]; trappedHeat[i] = 0;
                if (old >= 0)
                {
                    node.U += oldNodes[old].U; node.V += oldNodes[old].V;
                    node.PressureGuess += pressure[old];
                }
            }
            else trappedHeat[i] += q;
        }
        // Displace heat only across immediate open fine faces. Covered heat that
        // has no neighbouring gas is retained until the material moves away.
        for (int i = 0; i < trappedHeat.Length; i++)
        {
            if (trappedHeat[i] == 0) continue;
            int x = i % fineWidth, y = i / fineWidth, count = 0;
            for (int k = 0; k < 4; k++) if (NextAt(x + (k == 0 ? -1 : k == 1 ? 1 : 0),
                y + (k == 2 ? -1 : k == 3 ? 1 : 0)) >= 0) count++;
            if (count == 0) continue;
            double q = trappedHeat[i] / count; trappedHeat[i] = 0;
            for (int k = 0; k < 4; k++)
            {
                int next = NextAt(x + (k == 0 ? -1 : k == 1 ? 1 : 0), y + (k == 2 ? -1 : k == 3 ? 1 : 0));
                if (next >= 0) newNodes[next].Heat += q;
            }
        }
        foreach (ref var node in newNodes)
        { node.U /= node.Pixels; node.V /= node.Pixels; node.PressureGuess /= node.Pixels; }
        (cells, nextCells) = (nextCells, cells); (cellAtFine, nextCellAtFine) = (nextCellAtFine, cellAtFine);
        EnsureWorkspace();
        faces.Clear(); faceOrder.Clear(); faceLookup.Clear();
        for (int i = 0; i < cells.Count; i++)
        {
            var node = cells[i];
            AddBoundary(i, node.X + node.W, node.Y, node.H, false, false);
            AddBoundary(i, node.X, node.Y + node.H, node.W, true, false);
            // Interior left/top gas faces were already emitted by the neighbour.
            AddBoundary(i, node.X, node.Y, node.H, false, true);
            AddBoundary(i, node.X, node.Y, node.W, true, true);
        }
        // Keep the v7 serialized face layout: x normals in fine row order, then
        // y normals. Traversal can change without invalidating existing saves.
        CollectionsMarshal.AsSpan(faceOrder).Sort(CollectionsMarshal.AsSpan(faces));
        for (int i = 0; i < faces.Count; i++) faceLookup.Add((faces[i].A, faces[i].B, faces[i].Y), i);
        pressureLinks.Clear(); pressurePoints.Clear(); TotalAirVolume = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            sizes[i] = cells[i].Volume; inverseSizes[i] = 1 / sizes[i]; TotalAirVolume += sizes[i];
            pressure[i] = cells[i].PressureGuess;
            pressurePoints.Add(new(cells[i].X, cells[i].Y));
        }
        foreach (var face in faces) pressureLinks.Add(new(face.A, face.B, face.Area * face.InverseDistance));
        pressureSolver.Rebuild(cells.Count, CollectionsMarshal.AsSpan(pressureLinks), sizes, CollectionsMarshal.AsSpan(pressurePoints));
        CacheGeometryWeights();
        BuildSteamCoupling();
        ReconstructVelocity(); PublishViews();
    }

    private int NextAt(int x, int y) => x < 0 || y < 0 || x >= fineWidth || y >= fineHeight ? -1 : nextCellAtFine[y * fineWidth + x];

    private void AddVolume(int x, int y, int w, int h, int block)
    {
        int index = nextCells.Count;
        double volume = w * h * World.CellVolume;
        nextCells.Add(new() { X = x, Y = y, W = w, H = h, Block = block,
            Volume = volume, Capacity = volume * Density * SpecificHeat });
        for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) nextCellAtFine[yy * fineWidth + xx] = index;
    }

    private void AddBoundary(int inside, int x, int y, int length, bool vertical, bool reverse)
    {
        for (int start = 0; start < length;)
        {
            int other = BoundaryNeighbour(x, y, start, vertical, reverse), end = start + 1;
            while (end < length && BoundaryNeighbour(x, y, end, vertical, reverse) == other) end++;
            if (!reverse || other < 0)
                AddFace(reverse ? other : inside, reverse ? inside : other, vertical,
                    vertical ? x + start : x, vertical ? y : y + start, end - start);
            start = end;
        }
    }

    private int BoundaryNeighbour(int x, int y, int offset, bool vertical, bool reverse) => vertical
        ? VolumeIndexAtFine(x + offset, y - (reverse ? 1 : 0))
        : VolumeIndexAtFine(x - (reverse ? 1 : 0), y + offset);

    private void AddFace(int a, int b, bool vertical, int x, int y, int length)
    {
        if (a == b) return;
        var nodes = CollectionsMarshal.AsSpan(cells);
        bool boundary = vertical ? y == 0 : x == 0 || x == fineWidth;
        if (!boundary && (a < 0 || b < 0))
        {
            int inside = a >= 0 ? a : b;
            if (inside >= 0) nodes[inside].WallArea += FineArea * length;
            return;
        }
        double ca = a >= 0 ? (vertical ? nodes[a].CenterY : nodes[a].CenterX) : (vertical ? y : x) * World.CellSize;
        double cb = b >= 0 ? (vertical ? nodes[b].CenterY : nodes[b].CenterX) : (vertical ? y : x) * World.CellSize;
        double speedA = a >= 0 ? (vertical ? nodes[a].V : nodes[a].U) : 0;
        double speedB = b >= 0 ? (vertical ? nodes[b].V : nodes[b].U) : 0;
        int view = -1;
        if (vertical && (y % Scale == 0 || y == fineHeight)) view = (y == fineHeight ? Height : y / Scale) * Width + x / Scale;
        if (!vertical && (x % Scale == 0 || x == fineWidth)) view = y / Scale * (Width + 1) + (x == fineWidth ? Width : x / Scale);
        faces.Add(new() { A = a, B = b, Y = vertical, Area = FineArea * length, InverseDistance = 1 / Math.Abs(cb - ca),
            BlendA = a < 0 ? 0 : b < 0 ? 1 : Math.Abs(cb - (vertical ? y : x) * World.CellSize) / Math.Abs(cb - ca),
            View = view, Velocity = a < 0 ? speedB : b < 0 ? speedA : .5 * (speedA + speedB) });
        faceOrder.Add(vertical ? fineHeight * (fineWidth + 1) + y * fineWidth + x : y * (fineWidth + 1) + x);
    }

    private void EnsureWorkspace()
    {
        if (pressure.Length >= cells.Count) return;
        int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, cells.Count));
        pressure = new double[n]; rhs = new double[n]; sizes = new double[n]; delta = new double[n];
        du = new double[n]; dv = new double[n]; weightX = new double[n]; weightY = new double[n];
        inverseSizes = new double[n]; transportScale = new double[n]; wallDecay = new double[n];
    }

    private void CacheGeometryWeights()
    {
        // Areas/capacities change only with geometry. All momentum substeps and
        // published diagnostic views reuse these identical averaging weights.
        Array.Clear(weightX, 0, cells.Count); Array.Clear(weightY, 0, cells.Count);
        Array.Clear(coarseCapacity); Array.Clear(viewAreaU); Array.Clear(viewAreaV);
        foreach (var node in cells) coarseCapacity[node.Block] += node.Capacity;
        for (int i = 0; i < Blocked.Length; i++) Blocked[i] = coarseCapacity[i] == 0;
        foreach (var face in faces)
        {
            var weights = face.Y ? weightY : weightX;
            if (face.A >= 0) weights[face.A] += face.Area;
            if (face.B >= 0) weights[face.B] += face.Area;
            if (face.View >= 0) (face.Y ? viewAreaV : viewAreaU)[face.View] += face.Area;
        }
    }

    private void ReconstructVelocity()
    {
        var nodes = CollectionsMarshal.AsSpan(cells);
        foreach (ref var node in nodes) node.U = node.V = 0;
        foreach (var face in CollectionsMarshal.AsSpan(faces))
        {
            double weighted = face.Velocity * face.Area;
            if (face.Y)
            {
                if (face.A >= 0) nodes[face.A].V += weighted;
                if (face.B >= 0) nodes[face.B].V += weighted;
            }
            else
            {
                if (face.A >= 0) nodes[face.A].U += weighted;
                if (face.B >= 0) nodes[face.B].U += weighted;
            }
        }
        for (int i = 0; i < nodes.Length; i++)
        {
            if (weightX[i] > 0) nodes[i].U /= weightX[i];
            if (weightY[i] > 0) nodes[i].V /= weightY[i];
        }
    }

    private void PublishViews()
    {
        Array.Clear(Heat); Array.Clear(U); Array.Clear(V);
        foreach (var node in cells) Heat[node.Block] += node.Heat;
        foreach (var face in faces)
        {
            if (face.View < 0) continue;
            if (face.Y) V[face.View] += face.Velocity * face.Area;
            else U[face.View] += face.Velocity * face.Area;
        }
        for (int i = 0; i < U.Length; i++) if (viewAreaU[i] > 0) U[i] /= viewAreaU[i];
        for (int i = 0; i < V.Length; i++) if (viewAreaV[i] > 0) V[i] /= viewAreaV[i];
        U.CopyTo(publishedU, 0); V.CopyTo(publishedV, 0);
    }

    private void ImportVelocityEdits()
    {
        if (U.AsSpan().SequenceEqual(publishedU) && V.AsSpan().SequenceEqual(publishedV)) return;
        foreach (ref var face in CollectionsMarshal.AsSpan(faces))
        {
            if (face.View < 0) continue;
            double value = face.Y ? V[face.View] : U[face.View];
            double previous = face.Y ? publishedV[face.View] : publishedU[face.View];
            if (value == previous) continue;
            if (!double.IsFinite(value) || Math.Abs(value) > 100) throw new ArgumentException("Invalid edited air velocity.");
            face.Velocity = value;
        }
        ReconstructVelocity(); PublishViews();
    }
}
