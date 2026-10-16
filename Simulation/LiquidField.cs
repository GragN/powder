namespace Powder;

// Resolved bulk circulation on the particle grid. The moving, whole-cell free surface
// is handled by World; its normal velocity is zero during this convection solve.
// Each immiscible liquid has its own pressure domain. Heat crosses their interfaces
// by Fourier conduction, not by blending their identities.
public sealed partial class LiquidField
{
    public int Width { get; }
    public int Height { get; }
    public double[] U { get; }
    public double[] V { get; }
    public double DivergenceRms { get; private set; }
    private readonly int[] kind, tagU, tagV;
    private readonly bool[] solid;
    private readonly double[] nu, buoyancy, oldU, oldV, pressure, viscousPressure;
    private readonly double[] dm, de, ds, dr;
    private readonly GridEquation equation;
    private const double Dx = World.CellSize;

    public LiquidField(int width, int height)
    {
        Width = width; Height = height;
        int n = width * height;
        U = new double[(width + 1) * height]; V = new double[width * (height + 1)];
        oldU = new double[U.Length]; oldV = new double[V.Length];
        tagU = new int[U.Length]; tagV = new int[V.Length];
        kind = new int[n]; solid = new bool[n]; nu = new double[n]; buoyancy = new double[n]; pressure = new double[n]; viscousPressure = new double[n];
        dm = new double[n]; de = new double[n]; ds = new double[n]; dr = new double[n];
        equation = new GridEquation(Math.Max(U.Length, V.Length));
    }

    public void Reset()
    {
        Array.Clear(U); Array.Clear(V); Array.Clear(tagU); Array.Clear(tagV);
        Array.Clear(kind); Array.Clear(pressure); Array.Clear(viscousPressure); DivergenceRms = 0;
    }

    private bool Same(int a, int b) => kind[a] != 0 && kind[a] == kind[b];

    private int UpdateGeometry(World world)
    {
        int count = 0;
        for (int i = 0; i < kind.Length; i++)
        {
            ref readonly var p = ref world.Cells[i];
            int next = LiquidProperties.Kind(p);
            if (next != kind[i]) pressure[i] = viscousPressure[i] = 0;
            kind[i] = next;
            var state = Materials.Get(p.Element).State;
            solid[i] = state is MatterState.Solid or MatterState.Powder;
            if (next == 0) continue;
            count++;
            nu[i] = LiquidProperties.KinematicViscosity(p);
            buoyancy[i] = World.Gravity * (LiquidProperties.Density(p) / LiquidProperties.ReferenceDensity(p) - 1);
        }
        for (int y = 0; y < Height; y++)
        for (int x = 0; x <= Width; x++)
        {
            int f = y * (Width + 1) + x;
            int tag = x > 0 && x < Width && Same(y * Width + x - 1, y * Width + x) ? kind[y * Width + x] : 0;
            if (tag == 0 || tag != tagU[f]) U[f] = 0;
            tagU[f] = tag;
        }
        for (int y = 0; y <= Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int f = y * Width + x;
            int tag = y > 0 && y < Height && Same(f - Width, f) ? kind[f] : 0;
            if (tag == 0 || tag != tagV[f]) V[f] = 0;
            tagV[f] = tag;
        }
        return count;
    }

    public void Step(World world, double seconds)
    {
        if (seconds <= 0) return;
        if (UpdateGeometry(world) == 0) { DivergenceRms = 0; return; }
        U.CopyTo(oldU, 0); V.CopyTo(oldV, 0);
        AdvectFaces(U, tagU, true, seconds);
        AdvectFaces(V, tagV, false, seconds);
        // Balance hydrostatic buoyancy before applying wall viscosity. Diffusing the
        // unbalanced gravity impulse would create spurious currents in a resting tank.
        Project(seconds, pressure);
        DiffuseFaces(U, tagU, true, seconds);
        DiffuseFaces(V, tagV, false, seconds);
        Project(seconds, viscousPressure);
        Transport(world, seconds);
    }

    public (double X, double Y) VelocityAtFine(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return (0, 0);
        return ((U[y * (Width + 1) + x] + U[y * (Width + 1) + x + 1]) * .5,
            (V[y * Width + x] + V[(y + 1) * Width + x]) * .5);
    }

    private static double Sample(double[] field, int stride, int rows, double x, double y)
    {
        x = Math.Clamp(x, 0, stride - 1); y = Math.Clamp(y, 0, rows - 1);
        int a = (int)x, b = (int)y, c = Math.Min(a + 1, stride - 1), d = Math.Min(b + 1, rows - 1);
        double fx = x - a, fy = y - b;
        return (field[b * stride + a] * (1 - fx) + field[b * stride + c] * fx) * (1 - fy)
            + (field[d * stride + a] * (1 - fx) + field[d * stride + c] * fx) * fy;
    }

    private void AdvectFaces(double[] field, int[] tags, bool horizontal, double seconds)
    {
        int stride = horizontal ? Width + 1 : Width, rows = horizontal ? Height : Height + 1;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < stride; x++)
        {
            int f = y * stride + x, tag = tags[f];
            if (tag == 0) continue;
            double xx = x + (horizontal ? 0 : .5), yy = y + (horizontal ? .5 : 0);
            double vx = Sample(oldU, Width + 1, Height, xx, yy - .5);
            double vy = Sample(oldV, Width, Height + 1, xx - .5, yy);
            double dx = vx * seconds / Dx, dy = vy * seconds / Dx;
            int steps = Math.Max(1, (int)Math.Ceiling(2 * Math.Max(Math.Abs(dx), Math.Abs(dy))));
            double endX = xx, endY = yy;
            for (int n = 1; n <= steps; n++)
            {
                double px = xx - dx * n / steps, py = yy - dy * n / steps;
                int cx = (int)Math.Floor(px), cy = (int)Math.Floor(py);
                if (cx < 0 || cy < 0 || cx >= Width || cy >= Height || kind[cy * Width + cx] != tag) break;
                endX = px; endY = py;
            }
            field[f] = Sample(horizontal ? oldU : oldV, stride, rows,
                endX - (horizontal ? 0 : .5), endY - (horizontal ? .5 : 0));
            if (!horizontal) field[f] += .5 * (buoyancy[f - Width] + buoyancy[f]) * seconds;
        }
    }

    private double FaceNu(int x, int y, bool horizontal) => horizontal
        ? .5 * (nu[y * Width + x - 1] + nu[y * Width + x])
        : .5 * (nu[(y - 1) * Width + x] + nu[y * Width + x]);

    private bool SolidFace(int x, int y, bool horizontal)
    {
        bool Wall(int xx, int yy) => yy >= Height || xx >= 0 && xx < Width && yy >= 0 && solid[yy * Width + xx];
        return horizontal ? Wall(x - 1, y) || Wall(x, y) : Wall(x, y - 1) || Wall(x, y);
    }

    // Backward Euler removes the explicit viscosity timestep restriction, including
    // highly viscous silica. Solid walls are no-slip; exposed surfaces are stress-free.
    private void DiffuseFaces(double[] field, int[] tags, bool horizontal, double seconds)
    {
        int stride = horizontal ? Width + 1 : Width, rows = horizontal ? Height : Height + 1;
        equation.Count = 0; equation.Stride = stride;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < stride; x++)
        {
            int f = y * stride + x;
            if (tags[f] == 0) continue;
            equation.Active[equation.Count++] = f;
            equation.Rhs[f] = field[f];
            double diagonal = 1, viscosity = FaceNu(x, y, horizontal);
            double Coefficient(int xx, int yy)
            {
                if (xx < 0 || yy < 0 || xx >= stride || yy >= rows) return 0;
                int j = yy * stride + xx;
                if (tags[j] == tags[f])
                {
                    double other = FaceNu(xx, yy, horizontal);
                    double coefficient = seconds / (Dx * Dx) * 2 * viscosity * other / (viscosity + other);
                    diagonal += coefficient; return coefficient;
                }
                if (SolidFace(xx, yy, horizontal)) diagonal += 2 * seconds * viscosity / (Dx * Dx);
                return 0;
            }
            equation.West[f] = Coefficient(x - 1, y); equation.East[f] = Coefficient(x + 1, y);
            equation.North[f] = Coefficient(x, y - 1); equation.South[f] = Coefficient(x, y + 1);
            equation.Diagonal[f] = diagonal;
        }
        equation.Solve(field, 1e-9);
    }

    // Conservative donor-cell finite volumes, not an extra heat-diffusion coefficient.
    // Mass, salt, energy and the enthalpy reference travel together. There is no
    // relaxation to a prescribed temperature and no heat loss from internal fluxes.
    private void Transport(World world, double seconds)
    {
        double maxOutflow = 0;
        for (int i = 0; i < kind.Length; i++)
        {
            if (kind[i] == 0) continue;
            int x = i % Width, y = i / Width, f = y * (Width + 1) + x;
            double rate = Math.Max(0, -U[f]) + Math.Max(0, U[f + 1])
                + Math.Max(0, -V[i]) + Math.Max(0, V[i + Width]);
            maxOutflow = Math.Max(maxOutflow, rate / Dx);
        }
        int substeps = Math.Max(1, (int)Math.Ceiling(maxOutflow * seconds / .8));
        double dt = seconds / substeps;
        for (int step = 0; step < substeps; step++)
        {
            Array.Clear(dm); Array.Clear(de); Array.Clear(ds); Array.Clear(dr);
            void Flux(int a, int b, double fraction)
            {
                if (fraction == 0 || LiquidProperties.Kind(world.Cells[a]) != kind[a]
                    || LiquidProperties.Kind(world.Cells[b]) != kind[b]) return;
                if (fraction < 0) { (a, b) = (b, a); fraction = -fraction; }
                ref readonly var donor = ref world.Cells[a];
                double mass = fraction * donor.Mass, energy = fraction * donor.Energy;
                double salt = fraction * donor.DissolvedSalt, reference = mass * donor.EnthalpyOffset;
                dm[a] -= mass; dm[b] += mass; de[a] -= energy; de[b] += energy;
                ds[a] -= salt; ds[b] += salt; dr[a] -= reference; dr[b] += reference;
            }
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                if (kind[i] == 0) continue;
                if (x + 1 < Width && Same(i, i + 1)) Flux(i, i + 1, U[y * (Width + 1) + x + 1] * dt / Dx);
                if (y + 1 < Height && Same(i, i + Width)) Flux(i, i + Width, V[i + Width] * dt / Dx);
            }
            for (int i = 0; i < kind.Length; i++)
            {
                if (kind[i] == 0) continue;
                ref var p = ref world.Cells[i];
                double reference = p.Mass * p.EnthalpyOffset + dr[i];
                p.Mass += dm[i]; p.Energy += de[i]; p.DissolvedSalt += ds[i];
                p.EnthalpyOffset = reference / p.Mass;
                Thermodynamics.ResolvePhase(ref p);
            }
        }
    }

    public sealed record State(int Width, int Height, double[] U, double[] V);
    public State Capture() => new(Width, Height, (double[])U.Clone(), (double[])V.Clone());
    public void Restore(World world, State state)
    {
        if (state.Width != Width || state.Height != Height || state.U is null || state.V is null
            || state.U.Length != U.Length || state.V.Length != V.Length
            || state.U.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100)
            || state.V.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100))
            throw new InvalidDataException("Invalid liquid flow data.");
        UpdateGeometry(world); state.U.CopyTo(U, 0); state.V.CopyTo(V, 0);
        for (int i = 0; i < U.Length; i++) if (tagU[i] == 0) U[i] = 0;
        for (int i = 0; i < V.Length; i++) if (tagV[i] == 0) V[i] = 0;
        Array.Clear(pressure); Array.Clear(viscousPressure);
    }
}
