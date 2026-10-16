namespace Powder;

// Staggered MAC grid for low-Mach, approximately incompressible 2D air.
// Heat is stored relative to the outside temperature; interior transport is conservative.
public sealed partial class AirField
{
    public const int Scale = 4;
    public const double Density = 1.204, SpecificHeat = 1005, Conductivity = .0257;
    private const double Viscosity = 1.5e-5; // kinematic viscosity, m²/s
    public int Width { get; }
    public int Height { get; }
    public double Dx { get; }
    public double Dy { get; }
    public double Capacity { get; }
    public double AmbientTemperature { get; private set; } = 20;
    public double[] Heat { get; } // excess sensible heat, J
    public double[] U { get; }    // horizontal face velocity, m/s
    public double[] V { get; }    // vertical face velocity, positive down, m/s
    public bool[] Blocked { get; }
    public double BoundaryHeat { get; private set; } // net outward heat, J; cold outflow can be negative
    public double ExcessEnergy => Heat.Sum();
    public double DivergenceRms { get; private set; }
    public bool Active { get; private set; }
    private readonly int fineWidth, fineHeight;
    private readonly bool[] nextBlocked;
    private readonly bool[] openU, openV;
    private readonly int[] fineColumns, fineRows;
    private readonly double[] uWork, vWork, delta, pressure;
    private readonly GridEquation pressureEquation;
    private int obstacleCount;

    public AirField(int fineWidth, int fineHeight)
    {
        this.fineWidth = fineWidth; this.fineHeight = fineHeight;
        Width = (fineWidth + Scale - 1) / Scale; Height = (fineHeight + Scale - 1) / Scale;
        Dx = fineWidth * World.CellSize / Width; Dy = fineHeight * World.CellSize / Height;
        Capacity = Density * SpecificHeat * Dx * Dy * World.Depth;
        Heat = new double[Width * Height]; Blocked = new bool[Heat.Length]; nextBlocked = new bool[Heat.Length];
        U = new double[(Width + 1) * Height]; V = new double[Width * (Height + 1)];
        uWork = new double[U.Length]; vWork = new double[V.Length]; delta = new double[Heat.Length];
        pressure = new double[Heat.Length]; pressureEquation = new GridEquation(Heat.Length) { Stride = Width };
        openU = new bool[U.Length]; openV = new bool[V.Length];
        // The last coarse row/column may have fewer than Scale fine cells. These exact
        // inverse boundaries match IndexAtFine, including non-multiple dimensions.
        fineColumns = new int[Width + 1]; fineRows = new int[Height + 1];
        for (int x = 0; x <= Width; x++) fineColumns[x] = (x * fineWidth + Width - 1) / Width;
        for (int y = 0; y <= Height; y++) fineRows[y] = (y * fineHeight + Height - 1) / Height;
        RebuildGeometry();
    }

    public int IndexAtFine(int x, int y) => Math.Clamp(y * Height / fineHeight, 0, Height - 1) * Width
        + Math.Clamp(x * Width / fineWidth, 0, Width - 1);
    public double Temperature(int index) => AmbientTemperature + Heat[index] / Capacity;
    public double TemperatureAtFine(int x, int y) => Temperature(IndexAtFine(x, y));
    public void SetTemperature(int x, int y, double temperature)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || !double.IsFinite(temperature) || temperature < -273.15)
            throw new ArgumentOutOfRangeException(nameof(temperature));
        Heat[y * Width + x] = (temperature - AmbientTemperature) * Capacity;
    }
    public void Reset()
    {
        foreach (var a in new[] { Heat, U, V, pressure }) Array.Clear(a);
        Array.Clear(Blocked); obstacleCount = 0; BoundaryHeat = 0; DivergenceRms = 0;
        RebuildGeometry();
    }

    private bool Fluid(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && !Blocked[y * Width + x];

    public void UpdateObstacles(World world)
    {
        bool changed = false;
        int occupiedCount = 0;
        if (obstacleCount < Heat.Length / 8)
        {
            // A linear read is faster on a mostly empty canvas: Particle is large,
            // and visiting four interleaved fine rows would waste the CPU cache.
            Array.Clear(nextBlocked);
            for (int i = 0; i < world.Cells.Length; i++)
            {
                Element element = world.Cells[i].Element;
                if (element == Element.Empty) continue;
                MatterState state = Materials.Get(element).State;
                if (state is not (MatterState.Solid or MatterState.Powder or MatterState.Liquid)) continue;
                nextBlocked[IndexAtFine(i % fineWidth, i / fineWidth)] = true;
            }
            for (int i = 0; i < nextBlocked.Length; i++)
            {
                changed |= nextBlocked[i] != Blocked[i];
                if (nextBlocked[i]) occupiedCount++;
            }
        }
        else
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                // In a full tank, one condensed fine cell already closes this entire
                // coarse block. Stop reading its remaining particles immediately.
                bool occupied = false;
                for (int yy = fineRows[y]; yy < fineRows[y + 1] && !occupied; yy++)
                for (int xx = fineColumns[x]; xx < fineColumns[x + 1]; xx++)
                {
                    Element element = world.Cells[yy * fineWidth + xx].Element;
                    if (element == Element.Empty) continue;
                    MatterState state = Materials.Get(element).State;
                    if (state is not (MatterState.Solid or MatterState.Powder or MatterState.Liquid)) continue;
                    occupied = true;
                    break;
                }
                int i = y * Width + x;
                nextBlocked[i] = occupied;
                changed |= occupied != Blocked[i];
                if (occupied) occupiedCount++;
            }
        obstacleCount = occupiedCount;
        // Displaced warm/cold air gives its excess heat to adjacent fluid, never to a reset timer.
        for (int i = 0; i < Heat.Length; i++)
        {
            if (!nextBlocked[i] || Heat[i] == 0) continue;
            int x = i % Width, y = i / Width, count = 0;
            if (x > 0 && !nextBlocked[i - 1]) count++;
            if (x + 1 < Width && !nextBlocked[i + 1]) count++;
            if (y > 0 && !nextBlocked[i - Width]) count++;
            if (y + 1 < Height && !nextBlocked[i + Width]) count++;
            if (count == 0) continue; // retain unresolved trapped heat until the pocket reopens
            double q = Heat[i] / count; Heat[i] = 0;
            if (x > 0 && !nextBlocked[i - 1]) Heat[i - 1] += q;
            if (x + 1 < Width && !nextBlocked[i + 1]) Heat[i + 1] += q;
            if (y > 0 && !nextBlocked[i - Width]) Heat[i - Width] += q;
            if (y + 1 < Height && !nextBlocked[i + Width]) Heat[i + Width] += q;
        }
        if (!changed)
        {
            // Velocity arrays remain public for loading and tools. A restored or
            // externally edited closed face must never enter momentum advection.
            ZeroClosedFaces();
            return;
        }
        nextBlocked.CopyTo(Blocked, 0);
        RebuildGeometry();
    }

    private void RebuildGeometry()
    {
        // A static tank needs only one topology / pressure-factor build, rather than
        // rebuilding neighbor tests and the pressure stencil twice every frame.
        for (int y = 0; y < Height; y++)
        for (int x = 0; x <= Width; x++)
        {
            int i = y * (Width + 1) + x;
            openU[i] = (x == 0 || Fluid(x - 1, y)) && (x == Width || Fluid(x, y));
            if (!openU[i]) U[i] = 0;
        }
        for (int y = 0; y <= Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            openV[i] = y < Height && (y == 0 || Fluid(x, y - 1)) && Fluid(x, y);
            if (!openV[i]) V[i] = 0;
        }
        RebuildPressureGeometry();
    }

    private void ZeroClosedFaces()
    {
        for (int i = 0; i < U.Length; i++) if (!openU[i]) U[i] = 0;
        for (int i = 0; i < V.Length; i++) if (!openV[i]) V[i] = 0;
    }

    public void ExchangeSurface(World world, int x, int y, double seconds)
    {
        ref var p = ref world.At(x, y);
        Contact(world, ref p, x, y, -1, 0, seconds); Contact(world, ref p, x, y, 1, 0, seconds);
        Contact(world, ref p, x, y, 0, -1, seconds); Contact(world, ref p, x, y, 0, 1, seconds);
    }

    private void Contact(World world, ref Particle p, int x, int y, int nx, int ny, double seconds)
    {
        // Surface contact is resolved on the particle grid; narrow passages below the air-grid
        // resolution are closed, so even a thin material wall cannot leak an air current.
        for (int distance = 1; distance <= Scale * 2; distance++)
        {
            int xx = x + nx * distance, yy = y + ny * distance;
            if (!world.Inside(xx, yy))
            {
                // A face actually on an open edge touches the outside atmosphere.
                // Interior faces never use an infinite temperature reservoir.
                if (distance == 1 && yy < fineHeight)
                {
                    double boundaryK = LiquidProperties.Conductivity(p);
                    double gap = .5 * (nx != 0 ? Dx : Dy);
                    double boundaryG = World.CellSize * World.Depth / (.5 * World.CellSize / boundaryK + gap / Conductivity);
                    BoundaryHeat -= Thermodynamics.ExchangeWithReservoir(ref p, AmbientTemperature, boundaryG, seconds);
                }
                return;
            }
            MatterState state = Materials.Get(world.At(xx, yy).Element).State;
            if (state is MatterState.Solid or MatterState.Powder or MatterState.Liquid) return;
            int i = IndexAtFine(xx, yy);
            if (Blocked[i]) continue;
            double k = LiquidProperties.Conductivity(p);
            double airDistance = .5 * (nx != 0 ? Dx : Dy);
            double g = World.CellSize * World.Depth / (.5 * World.CellSize / k + airDistance / Conductivity);
            var air = new Particle { Element = Element.Air, Mass = Capacity / SpecificHeat,
                Energy = Capacity * AmbientTemperature + Heat[i] };
            double before = p.Energy;
            Thermodynamics.Exchange(ref p, ref air, seconds, g);
            Heat[i] += before - p.Energy;
            return;
        }
    }

    public (double X, double Y) Velocity(double x, double y)
        => (Sample(U, Width + 1, Height, x / Dx, y / Dy - .5),
            Sample(V, Width, Height + 1, x / Dx - .5, y / Dy));
    public (double X, double Y) VelocityAtFine(int x, int y)
        => Velocity((x + .5) * World.CellSize, (y + .5) * World.CellSize);

    private static double Sample(double[] a, int width, int height, double x, double y)
    {
        x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
        int ix = (int)x, iy = (int)y, jx = Math.Min(ix + 1, width - 1), jy = Math.Min(iy + 1, height - 1);
        double fx = x - ix, fy = y - iy;
        return (a[iy * width + ix] * (1 - fx) + a[iy * width + jx] * fx) * (1 - fy)
            + (a[jy * width + ix] * (1 - fx) + a[jy * width + jx] * fx) * fy;
    }

    private (double X, double Y) Departure(double x, double y, double dx, double dy)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(2 * Math.Max(Math.Abs(dx) / Dx, Math.Abs(dy) / Dy)));
        double lastX = x, lastY = y;
        for (int n = 1; n <= steps; n++)
        {
            double xx = x - dx * n / steps, yy = y - dy * n / steps;
            if (xx < 0 || yy < 0 || xx >= Width * Dx || yy >= Height * Dy) return (xx, yy);
            if (Blocked[(int)(yy / Dy) * Width + (int)(xx / Dx)]) return (lastX, lastY);
            lastX = xx; lastY = yy;
        }
        return (lastX, lastY);
    }

    public void Step(double seconds)
    {
        bool active = HasActivity(Heat, 1e-14) || HasActivity(U, 1e-10) || HasActivity(V, 1e-10);
        Active = active;
        if (!active) { DivergenceRms = 0; return; }
        AdvectMomentum(seconds);
        Project(seconds);
        TransportHeat(seconds);
    }

    private static bool HasActivity(double[] values, double tolerance)
    {
        foreach (double value in values) if (value > tolerance || value < -tolerance) return true;
        return false;
    }

    private void AdvectMomentum(double seconds)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x <= Width; x++)
        {
            int i = y * (Width + 1) + x;
            if (!openU[i]) { uWork[i] = 0; continue; }
            double px = x * Dx, py = (y + .5) * Dy;
            // The normal component already lies exactly on this staggered face.
            // Only the transverse component needs interpolation.
            double transverse = Sample(V, Width, Height + 1, x - .5, y + .5);
            var d = Departure(px, py, U[i] * seconds, transverse * seconds);
            double advected = d.X < 0 || d.Y < 0 || d.X >= Width * Dx || d.Y >= Height * Dy ? 0
                : Sample(U, Width + 1, Height, d.X / Dx, d.Y / Dy - .5);
            double lap = ((x > 0 ? U[i - 1] : U[i]) - 2 * U[i] + (x < Width ? U[i + 1] : U[i])) / (Dx * Dx)
                + ((y > 0 ? U[i - Width - 1] : U[i]) - 2 * U[i] + (y + 1 < Height ? U[i + Width + 1] : U[i])) / (Dy * Dy);
            uWork[i] = advected + Viscosity * seconds * lap;
        }
        for (int y = 0; y <= Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = y * Width + x;
            if (!openV[i]) { vWork[i] = 0; continue; }
            double px = (x + .5) * Dx, py = y * Dy;
            double transverse = Sample(U, Width + 1, Height, x + .5, y - .5);
            var d = Departure(px, py, transverse * seconds, V[i] * seconds);
            double advected = d.X < 0 || d.Y < 0 || d.X >= Width * Dx || d.Y >= Height * Dy ? 0
                : Sample(V, Width, Height + 1, d.X / Dx - .5, d.Y / Dy);
            double lap = ((x > 0 ? V[i - 1] : V[i]) - 2 * V[i] + (x + 1 < Width ? V[i + 1] : V[i])) / (Dx * Dx)
                + ((y > 0 ? V[i - Width] : V[i]) - 2 * V[i] + V[i + Width]) / (Dy * Dy);
            double t = y == 0 ? Temperature(x) : .5 * (Temperature((y - 1) * Width + x) + Temperature(y * Width + x));
            double buoyancy = World.Gravity * ((AmbientTemperature + 273.15) / Math.Max(50, t + 273.15) - 1);
            vWork[i] = advected + seconds * (Viscosity * lap + buoyancy);
        }
        uWork.CopyTo(U, 0); vWork.CopyTo(V, 0);
    }

    private void TransportHeat(double seconds)
    {
        double maxRate = 0;
        double diffusion = Conductivity / (Density * SpecificHeat);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Fluid(x, y)) continue;
            double rate = (Math.Max(0, U[y * (Width + 1) + x + 1]) + Math.Max(0, -U[y * (Width + 1) + x])) / Dx
                + (Math.Max(0, V[(y + 1) * Width + x]) + Math.Max(0, -V[y * Width + x])) / Dy;
            maxRate = Math.Max(maxRate, rate);
        }
        int substeps = Math.Max(1, (int)Math.Ceiling(seconds * (maxRate + 4 * diffusion * (1 / (Dx * Dx) + 1 / (Dy * Dy))) / .75));
        double dt = seconds / substeps;
        for (int step = 0; step < substeps; step++)
        {
            Array.Clear(delta);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x <= Width; x++)
            {
                if (!openU[y * (Width + 1) + x]) continue;
                int left = x > 0 ? y * Width + x - 1 : -1;
                int right = x < Width ? y * Width + x : -1;
                FaceFlux(left, right, U[y * (Width + 1) + x] * dt / Dx,
                    Conductivity * World.Depth * Dy / Dx * dt / Capacity * (x == 0 || x == Width ? 2 : 1));
            }
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (!openV[y * Width + x]) continue;
                int above = y > 0 ? (y - 1) * Width + x : -1;
                int below = y * Width + x;
                FaceFlux(above, below, V[y * Width + x] * dt / Dy,
                    Conductivity * World.Depth * Dx / Dy * dt / Capacity * (y == 0 ? 2 : 1));
            }
            for (int i = 0; i < Heat.Length; i++) Heat[i] += delta[i];
        }
    }

    private void FaceFlux(int a, int b, double courant, double diffusion)
    {
        double ha = a < 0 ? 0 : Heat[a], hb = b < 0 ? 0 : Heat[b];
        double q = courant * (courant > 0 ? ha : hb) + diffusion * (ha - hb);
        if (a >= 0) delta[a] -= q; else BoundaryHeat -= q;
        if (b >= 0) delta[b] += q; else BoundaryHeat += q;
    }
}
