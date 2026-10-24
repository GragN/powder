namespace Powder;

// A symmetric aggregation V-cycle is only a preconditioner: PCG still solves
// the original finite-volume equation at its original divergence tolerance.
internal sealed class PressureMultigrid
{
    private sealed class Level
    {
        public int Count;
        public bool Leaf, Dense;
        public int[] Rows = [], Columns = [], Parent = [], Roots = [], Groups = [], Cursor = [];
        public PressurePoint[] Points = [];
        public double[] Diagonal = [], Values = [], Inverse = [], X = [], B = [], Factor = [];
        public readonly Dictionary<long, double> Edges = [];
        public Level? Coarse;

        public void Ensure(int size)
        {
            if (Inverse.Length >= size) return;
            int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, size));
            Inverse = new double[n]; Parent = new int[n]; Roots = new int[n]; Groups = new int[n];
            Cursor = new int[n]; Points = new PressurePoint[n];
            Rows = new int[n + 1]; Diagonal = new double[n]; X = new double[n]; B = new double[n];
        }
    }

    private readonly Level fine = new();

    public void Rebuild(int count, int[] rows, int[] columns, double[] diagonal, double[] values,
        ReadOnlySpan<PressurePoint> points)
    {
        fine.Ensure(count); fine.Count = count;
        fine.Rows = rows; fine.Columns = columns; fine.Diagonal = diagonal; fine.Values = values;
        points.CopyTo(fine.Points);
        Build(fine, AirField.Scale * 2);
    }

    private static int Root(int[] roots, int i)
    {
        while (roots[i] != i) { roots[i] = roots[roots[i]]; i = roots[i]; }
        return i;
    }

    private static void Build(Level level, int span)
    {
        int count = level.Count;
        for (int i = 0; i < count; i++)
        {
            // A whole closed Neumann component can collapse to a constant mode.
            // It has no pressure gradient and needs no inverse of a zero diagonal.
            if (level.Rows[i] == level.Rows[i + 1] && level.Diagonal[i] < 1e-12) level.Diagonal[i] = 1;
            level.Inverse[i] = 1 / level.Diagonal[i];
        }
        if (count <= 48) { Factor(level); level.Leaf = level.Dense = true; return; }
        // Aggregate only nodes connected inside the same spatial block. A thin
        // wall cannot join two disconnected regions in a coarse correction.
        for (int i = 0; i < count; i++) { level.Roots[i] = i; level.Groups[i] = -1; }
        for (int i = 0; i < count; i++) for (int k = level.Rows[i]; k < level.Rows[i + 1]; k++)
        {
            int j = level.Columns[k];
            if (j <= i || level.Points[i].X / span != level.Points[j].X / span
                || level.Points[i].Y / span != level.Points[j].Y / span) continue;
            int a = Root(level.Roots, i), b = Root(level.Roots, j);
            if (a != b) level.Roots[b] = a;
        }
        int coarseCount = 0;
        for (int i = 0; i < count; i++)
        {
            int root = Root(level.Roots, i);
            if (level.Groups[root] < 0) level.Groups[root] = coarseCount++;
            level.Parent[i] = level.Groups[root];
        }
        if (coarseCount >= count * .95)
        { level.Leaf = true; level.Dense = false; return; }
        level.Leaf = false;
        var coarse = level.Coarse ??= new();
        coarse.Ensure(coarseCount); coarse.Count = coarseCount;
        Array.Clear(coarse.Diagonal, 0, coarseCount); Array.Clear(coarse.Rows, 0, coarseCount + 1);
        coarse.Edges.Clear();
        // Galerkin A_c = P^T A P: internal fluxes cancel, all other coefficients
        // are sums of the exact fine equation, including open boundaries/gauges.
        for (int i = 0; i < count; i++)
        {
            int a = level.Parent[i];
            coarse.Diagonal[a] += level.Diagonal[i]; coarse.Points[a] = level.Points[i];
            for (int k = level.Rows[i]; k < level.Rows[i + 1]; k++)
            {
                int j = level.Columns[k], b = level.Parent[j];
                if (a == b) coarse.Diagonal[a] += level.Values[k];
                else if (j > i)
                {
                    long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    coarse.Edges.TryGetValue(key, out double weight);
                    coarse.Edges[key] = weight + level.Values[k];
                }
            }
        }
        foreach (var edge in coarse.Edges)
        { coarse.Rows[(int)(edge.Key >> 32) + 1]++; coarse.Rows[(int)(edge.Key & uint.MaxValue) + 1]++; }
        for (int i = 0; i < coarseCount; i++)
        { coarse.Rows[i + 1] += coarse.Rows[i]; coarse.Cursor[i] = coarse.Rows[i]; }
        int entries = coarse.Rows[coarseCount];
        if (coarse.Columns.Length < entries)
        {
            int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, entries));
            coarse.Columns = new int[n]; coarse.Values = new double[n];
        }
        foreach (var edge in coarse.Edges)
        {
            int a = (int)(edge.Key >> 32), b = (int)(edge.Key & uint.MaxValue);
            int ia = coarse.Cursor[a]++, ib = coarse.Cursor[b]++;
            coarse.Columns[ia] = b; coarse.Columns[ib] = a;
            coarse.Values[ia] = coarse.Values[ib] = edge.Value;
        }
        for (int i = 0; i < coarseCount; i++)
            Array.Sort(coarse.Columns, coarse.Values, coarse.Rows[i], coarse.Rows[i + 1] - coarse.Rows[i]);
        Build(coarse, span * 2);
    }

    private static void Factor(Level level)
    {
        int n = level.Count;
        if (level.Factor.Length < n * n) level.Factor = new double[n * n];
        Array.Clear(level.Factor);
        for (int i = 0; i < n; i++)
        {
            level.Factor[i * n + i] = level.Diagonal[i];
            for (int k = level.Rows[i]; k < level.Rows[i + 1]; k++)
                level.Factor[i * n + level.Columns[k]] = level.Values[k];
        }
        for (int i = 0; i < n; i++) for (int j = 0; j <= i; j++)
        {
            double a = level.Factor[i * n + j];
            for (int k = 0; k < j; k++) a -= level.Factor[i * n + k] * level.Factor[j * n + k];
            level.Factor[i * n + j] = j == i ? Math.Sqrt(Math.Max(a, level.Diagonal[i] * 1e-14))
                : a / level.Factor[j * n + j];
        }
    }

    public void Apply(double[] rhs, double[] result)
    {
        fine.B = rhs; fine.X = result;
        Cycle(fine);
    }

    private static unsafe void Sweep(Level level, bool reverse)
    {
        fixed (int* rows = level.Rows, columns = level.Columns)
        fixed (double* b = level.B, x = level.X, values = level.Values, inverse = level.Inverse)
        for (int k = 0; k < level.Count; k++)
        {
            int i = reverse ? level.Count - 1 - k : k;
            double sum = b[i];
            for (int j = rows[i]; j < rows[i + 1]; j++) sum -= values[j] * x[columns[j]];
            x[i] = sum * inverse[i];
        }
    }

    private static unsafe void SweepFromZero(Level level)
    {
        // The first forward sweep starts from a zero correction. Sorted CSR
        // rows make every upper neighbour zero, so only the already-written
        // lower triangle is needed; the old correction need not be cleared.
        fixed (int* rows = level.Rows, columns = level.Columns)
        fixed (double* b = level.B, x = level.X, values = level.Values, inverse = level.Inverse)
        for (int i = 0; i < level.Count; i++)
        {
            double sum = b[i];
            for (int j = rows[i]; j < rows[i + 1] && columns[j] < i; j++) sum -= values[j] * x[columns[j]];
            x[i] = sum * inverse[i];
        }
    }

    private static unsafe void Cycle(Level level)
    {
        if (level.Leaf)
        {
            if (!level.Dense)
            {
                SweepFromZero(level); Sweep(level, true);
                for (int step = 1; step < 8; step++) { Sweep(level, false); Sweep(level, true); }
                return;
            }
            int n = level.Count;
            for (int i = 0; i < n; i++)
            {
                double sum = level.B[i];
                for (int j = 0; j < i; j++) sum -= level.Factor[i * n + j] * level.X[j];
                level.X[i] = sum / level.Factor[i * n + i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = level.X[i];
                for (int j = i + 1; j < n; j++) sum -= level.Factor[j * n + i] * level.X[j];
                level.X[i] = sum / level.Factor[i * n + i];
            }
            return;
        }
        var coarse = level.Coarse!;
        SweepFromZero(level); Array.Clear(coarse.B, 0, coarse.Count);
        fixed (int* rows = level.Rows, columns = level.Columns, parent = level.Parent)
        fixed (double* b = level.B, x = level.X, d = level.Diagonal, values = level.Values, target = coarse.B)
        for (int i = 0; i < level.Count; i++)
        {
            double residual = b[i] - d[i] * x[i];
            for (int k = rows[i]; k < rows[i + 1]; k++) residual -= values[k] * x[columns[k]];
            target[parent[i]] += residual;
        }
        Cycle(coarse);
        for (int i = 0; i < level.Count; i++) level.X[i] += coarse.X[level.Parent[i]];
        Sweep(level, true);
    }
}
