namespace Powder;

internal readonly record struct PressureLink(int A, int B, double Weight);

// Symmetric graph Laplacian with a cached IC(0) factor. Refinement changes the
// connectivity, not the pressure equation or its physical accuracy target.
internal sealed class SparsePressure
{
    private int count;
    private int[] rows = [], columns = [], reverse = [], cursor = [], parent = [], anchor = [];
    private bool[] open = [], gauge = [];
    public int LastIterations { get; private set; }
    private double[] diagonal = [], values = [], lower = [], inverseFactor = [], inverseVolume = [], r = [], z = [], p = [], ap = [], work = [];

    public void Rebuild(int size, ReadOnlySpan<PressureLink> links, double[] volumes)
    {
        count = size;
        if (diagonal.Length < size)
        {
            int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, size));
            rows = new int[n + 1]; cursor = new int[n]; diagonal = new double[n]; inverseFactor = new double[n]; inverseVolume = new double[n];
            parent = new int[n]; anchor = new int[n]; open = new bool[n]; gauge = new bool[n];
            r = new double[n]; z = new double[n]; p = new double[n]; ap = new double[n]; work = new double[n];
        }
        if (rows.Length == 0) rows = new int[1];
        Array.Clear(rows, 0, size + 1); Array.Clear(diagonal, 0, size);
        Array.Clear(open, 0, size); Array.Clear(gauge, 0, size);
        for (int i = 0; i < size; i++) { parent[i] = i; anchor[i] = -1; inverseVolume[i] = 1 / volumes[i]; }
        foreach (var edge in links) if (edge.A >= 0 && edge.B >= 0)
        {
            int a = Root(edge.A), b = Root(edge.B);
            if (a != b) parent[b] = a;
        }
        foreach (var edge in links) if (edge.A < 0 || edge.B < 0) open[Root(Math.Max(edge.A, edge.B))] = true;
        for (int i = 0; i < size; i++)
        {
            int root = Root(i);
            if (anchor[root] < 0 || volumes[i] > volumes[anchor[root]]) anchor[root] = i;
        }
        for (int i = 0; i < size; i++) if (anchor[i] >= 0 && !open[i]) gauge[anchor[i]] = true;
        foreach (var edge in links)
        {
            if (edge.A >= 0) { diagonal[edge.A] += edge.Weight; if (edge.B >= 0 && !gauge[edge.A] && !gauge[edge.B]) rows[edge.A + 1]++; }
            if (edge.B >= 0) { diagonal[edge.B] += edge.Weight; if (edge.A >= 0 && !gauge[edge.A] && !gauge[edge.B]) rows[edge.B + 1]++; }
        }
        for (int i = 0; i < size; i++) { rows[i + 1] += rows[i]; cursor[i] = rows[i]; if (gauge[i] || diagonal[i] == 0) diagonal[i] = 1; }
        int entries = rows[size];
        if (columns.Length < entries)
        {
            int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, entries));
            columns = new int[n]; reverse = new int[n]; values = new double[n]; lower = new double[n];
        }
        foreach (var edge in links)
        {
            if (edge.A < 0 || edge.B < 0 || gauge[edge.A] || gauge[edge.B]) continue;
            int a = cursor[edge.A]++, b = cursor[edge.B]++;
            columns[a] = edge.B; columns[b] = edge.A; values[a] = values[b] = -edge.Weight;
        }
        for (int i = 0; i < size; i++) Array.Sort(columns, values, rows[i], rows[i + 1] - rows[i]);
        for (int i = 0; i < size; i++) for (int k = rows[i]; k < rows[i + 1]; k++)
        {
            int j = columns[k];
            reverse[k] = Array.BinarySearch(columns, rows[j], rows[j + 1] - rows[j], i);
            if (reverse[k] < 0) throw new InvalidOperationException("Asymmetric air-pressure graph.");
        }
        Array.Clear(lower, 0, entries);
        for (int i = 0; i < size; i++)
        {
            double d = diagonal[i];
            for (int k = rows[i]; k < rows[i + 1] && columns[k] < i; k++)
            {
                int j = columns[k], a = rows[i], b = rows[j];
                double v = values[k];
                while (a < k && b < rows[j + 1] && columns[b] < j)
                {
                    if (columns[a] == columns[b]) { v -= lower[a] * lower[b]; a++; b++; }
                    else if (columns[a] < columns[b]) a++;
                    else b++;
                }
                lower[k] = v * inverseFactor[j]; d -= lower[k] * lower[k];
            }
            inverseFactor[i] = 1 / Math.Sqrt(Math.Max(d, diagonal[i] * .01));
        }
    }

    private int Root(int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    private unsafe void Multiply(double[] x, double[] output)
    {
        // Rebuild validates all graph indices and symmetric reverse entries.
        // These buffers stay pinned only for the duration of the hot loop.
        fixed (int* row = rows, col = columns)
        fixed (double* d = diagonal, a = values, source = x, target = output)
        for (int i = 0; i < count; i++)
        {
            double sum = d[i] * source[i];
            for (int k = row[i]; k < row[i + 1]; k++) sum += a[k] * source[col[k]];
            target[i] = sum;
        }
    }

    private unsafe double Precondition()
    {
        fixed (int* row = rows, col = columns, other = reverse)
        fixed (double* residual = r, result = z, forward = work, l = lower, inv = inverseFactor)
        {
            for (int i = 0; i < count; i++)
            {
                double sum = residual[i];
                for (int k = row[i]; k < row[i + 1] && col[k] < i; k++) sum -= l[k] * forward[col[k]];
                forward[i] = sum * inv[i];
            }
            double dot = 0;
            for (int i = count - 1; i >= 0; i--)
            {
                double sum = forward[i];
                for (int k = row[i + 1] - 1; k >= row[i] && col[k] > i; k--) sum -= l[other[k]] * result[col[k]];
                result[i] = sum * inv[i]; dot += residual[i] * result[i];
            }
            return dot;
        }
    }

    public unsafe void Solve(double[] rhs, double[] solution, double[] volumes, double divergenceTolerance, double seconds)
    {
        LastIterations = 0;
        for (int i = 0; i < count; i++) if (gauge[i]) solution[i] = 0;
        Multiply(solution, ap);
        fixed (double* b = rhs, x = solution, residual = r, direction = p, product = ap, preconditioned = z,
            inverseV = inverseVolume, volume = volumes)
        {
            double totalVolume = 0, norm = 0;
            for (int i = 0; i < count; i++)
            { residual[i] = gauge[i] ? 0 : b[i] - product[i]; norm += residual[i] * residual[i] * inverseV[i]; totalVolume += volume[i]; }
            double target = Math.Pow(divergenceTolerance / seconds, 2) * totalVolume;
            if (norm <= target) return;
            double rz = Precondition();
            for (int i = 0; i < count; i++) direction[i] = preconditioned[i];
            for (int step = 0; step < 480 && rz > 1e-300; step++)
            {
                LastIterations = step + 1;
                Multiply(p, ap);
                double denominator = 0;
                for (int i = 0; i < count; i++) denominator += direction[i] * product[i];
                if (!(denominator > 0)) break;
                double alpha = rz / denominator; norm = 0;
                for (int i = 0; i < count; i++)
                { x[i] += alpha * direction[i]; residual[i] -= alpha * product[i]; norm += residual[i] * residual[i] * inverseV[i]; }
                if (norm <= target) break;
                double next = Precondition(), beta = next / rz;
                for (int i = 0; i < count; i++) direction[i] = preconditioned[i] + beta * direction[i];
                rz = next;
            }
        }
    }
}
