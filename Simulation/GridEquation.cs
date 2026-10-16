namespace Powder;

// Reused matrix workspace for symmetric five-point pressure / implicit-viscosity systems.
// Cached incomplete Cholesky and optional aggregation multigrid preconditioners.
internal sealed class GridEquation(int size)
{
    public readonly double[] Diagonal = new double[size], West = new double[size], East = new double[size],
        North = new double[size], South = new double[size], Rhs = new double[size];
    public readonly int[] Active = new int[size];
    private readonly double[] residual = new double[size], direction = new double[size], product = new double[size],
        inverseFactor = new double[size], forward = new double[size], preconditioned = new double[size];
    public int Count, Stride;
    public bool UseMultigrid { get; set; }
    private bool matrixChanged = true;
    private Multigrid? multigrid;

    // A constant geometry reuses its preconditioner. Call this after changing any
    // coefficient, active index or stride; changing only the right hand side is free.
    public void MatrixChanged() => matrixChanged = true;

    private unsafe void Multiply(double[] x, double[] result)
    {
        // Stencils are constructed internally: a nonzero coefficient always has a
        // valid active neighbor. Pinning these hot loops removes repeated bounds checks.
        fixed (int* active = Active)
        fixed (double* d = Diagonal, w = West, e = East, n = North, s = South, values = x, output = result)
        for (int k = 0; k < Count; k++)
        {
            int i = active[k];
            double value = d[i] * values[i];
            if (w[i] != 0) value -= w[i] * values[i - 1];
            if (e[i] != 0) value -= e[i] * values[i + 1];
            if (n[i] != 0) value -= n[i] * values[i - Stride];
            if (s[i] != 0) value -= s[i] * values[i + Stride];
            output[i] = value;
        }
    }

    private void Factor()
    {
        // Active indices are in ascending row order. Missing neighbors have zero coefficients.
        for (int k = 0; k < Count; k++)
        {
            int i = Active[k];
            double d = Diagonal[i];
            if (West[i] != 0) d -= West[i] * West[i] * inverseFactor[i - 1];
            if (North[i] != 0) d -= North[i] * North[i] * inverseFactor[i - Stride];
            inverseFactor[i] = 1 / Math.Max(d, .01 * Diagonal[i]);
        }
    }

    private unsafe double Precondition()
    {
        fixed (int* active = Active)
        fixed (double* r = residual, inv = inverseFactor, f = forward, z = preconditioned,
            w = West, e = East, n = North, s = South)
        {
            if (multigrid is not null)
            {
                multigrid.Apply(residual, preconditioned);
                double result = 0;
                for (int k = 0; k < Count; k++) { int i = active[k]; result += r[i] * z[i]; }
                return result;
            }
            for (int k = 0; k < Count; k++)
            {
                int i = active[k];
                double value = r[i];
                if (w[i] != 0) value += w[i] * inv[i - 1] * f[i - 1];
                if (n[i] != 0) value += n[i] * inv[i - Stride] * f[i - Stride];
                f[i] = value;
            }
            double dot = 0;
            for (int k = Count - 1; k >= 0; k--)
            {
                int i = active[k];
                double value = f[i];
                if (e[i] != 0) value += e[i] * z[i + 1];
                if (s[i] != 0) value += s[i] * z[i + Stride];
                z[i] = value * inv[i]; dot += r[i] * z[i];
            }
            return dot;
        }
    }

    public unsafe void Solve(double[] x, double tolerance, int iterations = 240)
    {
        fixed (int* active = Active)
        fixed (double* rhs = Rhs, values = x, r = residual, p = product, d = direction, z = preconditioned)
        {
            Multiply(x, product);
            double norm = 0, target = tolerance * tolerance * Count;
            for (int k = 0; k < Count; k++)
            {
                int i = active[k]; r[i] = rhs[i] - p[i]; norm += r[i] * r[i];
            }
            if (norm <= target) return;
            if (matrixChanged)
            {
                if (UseMultigrid && Count > 512)
                {
                    if (multigrid is null) multigrid = new Multigrid(this);
                    else multigrid.Update(this);
                }
                else { multigrid = null; Factor(); }
                matrixChanged = false;
            }
            double rz = Precondition();
            for (int k = 0; k < Count; k++) { int i = active[k]; d[i] = z[i]; }
            for (int step = 0; step < iterations && rz > 1e-28; step++)
            {
                Multiply(direction, product);
                double denominator = 0;
                for (int k = 0; k < Count; k++) { int i = active[k]; denominator += d[i] * p[i]; }
                if (denominator <= 1e-30) break;
                double alpha = rz / denominator;
                norm = 0;
                for (int k = 0; k < Count; k++)
                {
                    int i = active[k];
                    values[i] += alpha * d[i]; r[i] -= alpha * p[i]; norm += r[i] * r[i];
                }
                if (norm <= target) break;
                double next = Precondition(), beta = next / rz;
                for (int k = 0; k < Count; k++) { int i = active[k]; d[i] = z[i] + beta * d[i]; }
                rz = next;
            }
        }
    }

    // Symmetric geometric aggregation V-cycle, used only as a PCG preconditioner.
    // Galerkin coefficients are sums of the original face coefficients: thin walls
    // and separate liquids remain in the fine pressure equation at full resolution.
    private sealed class Multigrid
    {
        private sealed class Level
        {
            public required int Width, Rows, Count;
            public required int[] Active;
            public required double[] D, W, E, N, S;
            public required double[] X, B;
            public int[]? Present;
            public int[]? Parent;
            public Level? Coarse;
        }

        private readonly Level fine;
        public Multigrid(GridEquation matrix)
        {
            fine = new Level
            {
                Width = matrix.Stride, Rows = matrix.Count == 0 ? 0 : matrix.Active[matrix.Count - 1] / matrix.Stride + 1,
                Count = matrix.Count, Active = matrix.Active,
                D = matrix.Diagonal, W = matrix.West, E = matrix.East, N = matrix.North, S = matrix.South,
                X = [], B = []
            };
            Build(fine);
        }

        public void Update(GridEquation matrix)
        {
            fine.Width = matrix.Stride; fine.Rows = matrix.Count == 0 ? 0 : matrix.Active[matrix.Count - 1] / matrix.Stride + 1;
            fine.Count = matrix.Count;
            Build(fine);
        }

        private static void Build(Level level)
        {
            if (level.Count <= 48 || level.Width <= 2 || level.Rows <= 2) { level.Coarse = null; return; }
            int width = (level.Width + 1) / 2, rows = (level.Rows + 1) / 2, size = width * rows;
            var coarse = level.Coarse;
            if (coarse is null || coarse.Width != width || coarse.D.Length < size) coarse = new Level
            {
                Width = width, Rows = rows, Count = 0, Active = new int[size],
                D = new double[size], W = new double[size], E = new double[size], N = new double[size], S = new double[size],
                X = new double[size], B = new double[size], Present = new int[size]
            };
            else
            {
                coarse.Rows = rows; coarse.Count = 0;
                Array.Clear(coarse.D); Array.Clear(coarse.W); Array.Clear(coarse.E); Array.Clear(coarse.N); Array.Clear(coarse.S);
                Array.Clear(coarse.Present!);
            }
            var present = coarse.Present!;
            if (level.Parent is null || level.Parent.Length < level.Width * level.Rows) level.Parent = new int[level.Width * level.Rows];
            for (int k = 0; k < level.Count; k++)
            {
                int i = level.Active[k], x = i % level.Width, y = i / level.Width;
                int p = y / 2 * width + x / 2;
                level.Parent[i] = p; present[p] = 1;
                // Internal aggregate edges cancel from P^T A P; only their
                // uncancelled diagonal (Dirichlet or identity term) remains.
                coarse.D[p] += level.D[i] - level.W[i] - level.E[i] - level.N[i] - level.S[i];
                if (x % 2 == 0 && level.W[i] != 0) { coarse.W[p] += level.W[i]; coarse.D[p] += level.W[i]; }
                if (x % 2 == 1 && level.E[i] != 0) { coarse.E[p] += level.E[i]; coarse.D[p] += level.E[i]; }
                if (y % 2 == 0 && level.N[i] != 0) { coarse.N[p] += level.N[i]; coarse.D[p] += level.N[i]; }
                if (y % 2 == 1 && level.S[i] != 0) { coarse.S[p] += level.S[i]; coarse.D[p] += level.S[i]; }
            }
            for (int i = 0; i < size; i++) if (present[i] != 0)
            {
                coarse.Active[coarse.Count++] = i;
                // A whole Neumann component can collapse to one constant mode.
                // Its Laplacian is zero: do not amplify roundoff in its compatible
                // right hand side by dividing by a vanishing artificial diagonal.
                if (coarse.D[i] <= 0) coarse.D[i] = 1;
            }
            level.Coarse = coarse;
            Build(coarse);
        }

        public void Apply(double[] rhs, double[] result)
        {
            fine.B = rhs; fine.X = result;
            Cycle(fine);
        }

        private static unsafe void Sweep(Level level, bool reverse)
        {
            int width = level.Width, count = level.Count;
            fixed (int* active = level.Active)
            fixed (double* d = level.D, w = level.W, e = level.E, n = level.N, s = level.S, b = level.B, x = level.X)
            for (int k = 0; k < count; k++)
            {
                int i = active[reverse ? count - 1 - k : k];
                double value = b[i];
                if (w[i] != 0) value += w[i] * x[i - 1];
                if (e[i] != 0) value += e[i] * x[i + 1];
                if (n[i] != 0) value += n[i] * x[i - width];
                if (s[i] != 0) value += s[i] * x[i + width];
                x[i] = value / d[i];
            }
        }

        private static unsafe void Cycle(Level level)
        {
            for (int k = 0; k < level.Count; k++) level.X[level.Active[k]] = 0;
            if (level.Coarse is not { } coarse)
            {
                for (int n = 0; n < 16; n++) { Sweep(level, false); Sweep(level, true); }
                return;
            }
            Sweep(level, false);
            for (int k = 0; k < coarse.Count; k++) coarse.B[coarse.Active[k]] = 0;
            fixed (int* active = level.Active, parent = level.Parent)
            fixed (double* d = level.D, w = level.W, e = level.E, n = level.N, s = level.S,
                b = level.B, x = level.X, coarseB = coarse.B)
            for (int k = 0; k < level.Count; k++)
            {
                int i = active[k];
                double r = b[i] - d[i] * x[i];
                if (w[i] != 0) r += w[i] * x[i - 1];
                if (e[i] != 0) r += e[i] * x[i + 1];
                if (n[i] != 0) r += n[i] * x[i - level.Width];
                if (s[i] != 0) r += s[i] * x[i + level.Width];
                coarseB[parent[i]] += r;
            }
            Cycle(coarse);
            for (int k = 0; k < level.Count; k++)
            {
                int i = level.Active[k]; level.X[i] += coarse.X[level.Parent![i]];
            }
            Sweep(level, true);
        }
    }
}
