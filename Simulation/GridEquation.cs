namespace Powder;

// Reused matrix workspace for symmetric five-point pressure / implicit-viscosity systems.
// Incomplete Cholesky preconditioning makes large, closed liquid tanks practical.
internal sealed class GridEquation(int size)
{
    public readonly double[] Diagonal = new double[size], West = new double[size], East = new double[size],
        North = new double[size], South = new double[size], Rhs = new double[size];
    public readonly int[] Active = new int[size];
    private readonly double[] residual = new double[size], direction = new double[size], product = new double[size],
        inverseFactor = new double[size], forward = new double[size], preconditioned = new double[size];
    public int Count, Stride;

    private void Multiply(double[] x, double[] result)
    {
        for (int k = 0; k < Count; k++)
        {
            int i = Active[k];
            double value = Diagonal[i] * x[i];
            if (West[i] != 0) value -= West[i] * x[i - 1];
            if (East[i] != 0) value -= East[i] * x[i + 1];
            if (North[i] != 0) value -= North[i] * x[i - Stride];
            if (South[i] != 0) value -= South[i] * x[i + Stride];
            result[i] = value;
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

    private double Precondition()
    {
        for (int k = 0; k < Count; k++)
        {
            int i = Active[k];
            double value = residual[i];
            if (West[i] != 0) value += West[i] * inverseFactor[i - 1] * forward[i - 1];
            if (North[i] != 0) value += North[i] * inverseFactor[i - Stride] * forward[i - Stride];
            forward[i] = value;
        }
        double dot = 0;
        for (int k = Count - 1; k >= 0; k--)
        {
            int i = Active[k];
            double value = forward[i];
            if (East[i] != 0) value += East[i] * preconditioned[i + 1];
            if (South[i] != 0) value += South[i] * preconditioned[i + Stride];
            preconditioned[i] = value * inverseFactor[i]; dot += residual[i] * preconditioned[i];
        }
        return dot;
    }

    public void Solve(double[] x, double tolerance, int iterations = 240)
    {
        Multiply(x, product);
        double norm = 0, target = tolerance * tolerance * Count;
        for (int k = 0; k < Count; k++)
        {
            int i = Active[k]; residual[i] = Rhs[i] - product[i]; norm += residual[i] * residual[i];
        }
        if (norm <= target) return;
        Factor();
        double rz = Precondition();
        for (int k = 0; k < Count; k++) { int i = Active[k]; direction[i] = preconditioned[i]; }
        for (int step = 0; step < iterations && rz > 1e-28; step++)
        {
            Multiply(direction, product);
            double denominator = 0;
            for (int k = 0; k < Count; k++) { int i = Active[k]; denominator += direction[i] * product[i]; }
            if (denominator <= 1e-30) break;
            double alpha = rz / denominator;
            norm = 0;
            for (int k = 0; k < Count; k++)
            {
                int i = Active[k];
                x[i] += alpha * direction[i]; residual[i] -= alpha * product[i]; norm += residual[i] * residual[i];
            }
            if (norm <= target) break;
            double next = Precondition(), beta = next / rz;
            for (int k = 0; k < Count; k++) { int i = Active[k]; direction[i] = preconditioned[i] + beta * direction[i]; }
            rz = next;
        }
    }
}
