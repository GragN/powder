using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Powder;

// Embedded-boundary finite volumes: large empty rectangles and particle-sized
// air volumes in mixed blocks. Every flux uses a real open face and air volume.
public sealed partial class AirField
{
    public const int Scale = 4;
    public const double Density = 1.204, SpecificHeat = 1005, Conductivity = .0257;
    private const double Viscosity = 1.5e-5;
    private const double FineArea = World.CellSize * World.Depth;
    public int Width { get; }
    public int Height { get; }
    public double Dx => Scale * World.CellSize;
    public double Dy => Scale * World.CellSize;
    public double Capacity => Density * SpecificHeat * Scale * Scale * World.CellVolume;
    public double AmbientTemperature { get; private set; } = 20;
    // Coarse diagnostic views; the finite-volume state is stored separately.
    public double[] Heat { get; }
    public double[] U { get; }
    public double[] V { get; }
    public bool[] Blocked { get; }
    public double BoundaryHeat { get; private set; }
    public double ExcessEnergy => Heat.Sum() + trappedHeat.Sum();
    public double DivergenceRms { get; private set; }
    public double MaximumDivergence { get; private set; }
    public bool Active { get; private set; }
    public int ControlVolumeCount => cells.Count;
    public int OpenFaceCount => faces.Count;
    public int PressureIterations => pressureSolver.LastIterations;
    public double TotalAirVolume { get; private set; }
    public bool ProfileSteps { get; set; }
    public readonly record struct Timings(double Momentum, double Pressure, double Heat);
    public Timings LastTimings { get; private set; }
    private readonly int fineWidth, fineHeight;
    private int[] cellAtFine, nextCellAtFine;
    private bool[] solid, nextSolid;
    private readonly double[] trappedHeat, coarseCapacity, publishedU, publishedV, viewAreaU, viewAreaV;
    private List<AirCell> cells = [], nextCells = [];
    private readonly List<AirFace> faces = [];
    private readonly Dictionary<(int A, int B, bool Y), int> faceLookup = [];
    private readonly SparsePressure pressureSolver = new();
    private double[] pressure = [], rhs = [], sizes = [], delta = [], du = [], dv = [], weightX = [], weightY = [];
    private double[] inverseSizes = [], transportScale = [], wallDecay = [];

    private struct AirCell
    {
        public int X, Y, W, H, Block;
        public double Volume, Capacity, Heat, U, V, WallArea, PressureGuess;
        public readonly int Pixels => W * H;
        public readonly double CenterX => (X + W * .5) * World.CellSize;
        public readonly double CenterY => (Y + H * .5) * World.CellSize;
    }

    private struct AirFace
    {
        public int A, B, View;
        public bool Y;
        public double Area, InverseDistance, Velocity, BlendA;
    }

    public AirField(int fineWidth, int fineHeight)
    {
        this.fineWidth = fineWidth; this.fineHeight = fineHeight;
        Width = (fineWidth + Scale - 1) / Scale; Height = (fineHeight + Scale - 1) / Scale;
        Heat = new double[Width * Height]; Blocked = new bool[Heat.Length]; coarseCapacity = new double[Heat.Length];
        U = new double[(Width + 1) * Height]; V = new double[Width * (Height + 1)];
        publishedU = new double[U.Length]; publishedV = new double[V.Length];
        viewAreaU = new double[U.Length]; viewAreaV = new double[V.Length];
        int count = fineWidth * fineHeight;
        cellAtFine = new int[count]; nextCellAtFine = new int[count]; Array.Fill(cellAtFine, -1);
        solid = new bool[count]; nextSolid = new bool[count]; trappedHeat = new double[count];
        RebuildGeometry();
    }

    public int IndexAtFine(int x, int y) => Math.Clamp(y / Scale, 0, Height - 1) * Width + Math.Clamp(x / Scale, 0, Width - 1);
    public int VolumeIndexAtFine(int x, int y) => x < 0 || y < 0 || x >= fineWidth || y >= fineHeight ? -1 : cellAtFine[y * fineWidth + x];
    public double VolumeAtFine(int x, int y) => VolumeIndexAtFine(x, y) is var i && i >= 0 ? cells[i].Volume : 0;
    public double CapacityAtFine(int x, int y) => VolumeAtFine(x, y) * Density * SpecificHeat;
    public double Temperature(int index) => coarseCapacity[index] > 0 ? AmbientTemperature + Heat[index] / coarseCapacity[index] : AmbientTemperature;
    public double TemperatureAtFine(int x, int y) => VolumeIndexAtFine(x, y) is var i && i >= 0 ? CellTemperature(i) : AmbientTemperature;
    private double CellTemperature(int i) => AmbientTemperature + cells[i].Heat / cells[i].Capacity;

    public void SetTemperature(int x, int y, double temperature)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException(nameof(x));
        ValidateTemperature(temperature);
        int block = y * Width + x;
        var nodes = CollectionsMarshal.AsSpan(cells);
        for (int i = 0; i < nodes.Length; i++) if (nodes[i].Block == block) SetCellTemperature(i, temperature);
    }

    public void SetTemperatureAtFine(int x, int y, double temperature)
    {
        ValidateTemperature(temperature);
        int i = VolumeIndexAtFine(x, y);
        if (i < 0) throw new ArgumentException("There is no air at this location.");
        SetCellTemperature(i, temperature);
    }

    private static void ValidateTemperature(double t)
    {
        if (!double.IsFinite(t) || t < -273.15) throw new ArgumentOutOfRangeException(nameof(t));
    }

    private void SetCellTemperature(int i, double t)
    {
        ref var node = ref CollectionsMarshal.AsSpan(cells)[i];
        double energy = (t - AmbientTemperature) * node.Capacity;
        Heat[node.Block] += energy - node.Heat; node.Heat = energy;
    }

    public void SetUniformVelocity(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 100 || Math.Abs(y) > 100)
            throw new ArgumentOutOfRangeException(nameof(x));
        foreach (ref var face in CollectionsMarshal.AsSpan(faces)) face.Velocity = face.Y ? y : x;
        ReconstructVelocity(); PublishViews();
    }

    public (double X, double Y) Velocity(double x, double y) => VelocityAtFine((int)Math.Floor(x / World.CellSize), (int)Math.Floor(y / World.CellSize));
    public (double X, double Y) VelocityAtFine(int x, int y)
    {
        int i = VolumeIndexAtFine(x, y);
        return i < 0 ? (0, 0) : (cells[i].U, cells[i].V);
    }

    // Fine-face diagnostics use the same connectivity and velocity as transport.
    // vertical=true denotes a horizontal face whose normal points downwards.
    public double NormalVelocityAtFineFace(int x, int y, bool vertical)
    {
        int a = vertical ? VolumeIndexAtFine(x, y - 1) : VolumeIndexAtFine(x - 1, y);
        int b = VolumeIndexAtFine(x, y);
        if (a == b) return a < 0 ? 0 : vertical ? cells[a].V : cells[a].U;
        return faceLookup.TryGetValue((a, b, vertical), out int i) ? faces[i].Velocity : 0;
    }

    public void Reset()
    {
        Array.Clear(solid); Array.Clear(nextSolid); Array.Clear(trappedHeat);
        cells.Clear(); faces.Clear(); Array.Fill(cellAtFine, -1);
        BoundaryHeat = DivergenceRms = MaximumDivergence = 0; Active = false;
        RebuildGeometry();
    }

    public void Step(double seconds)
    {
        LastTimings = default;
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        ImportVelocityEdits();
        Active = cells.Any(c => Math.Abs(c.Heat) > 1e-14) || faces.Any(f => Math.Abs(f.Velocity) > 1e-10);
        if (!Active || seconds == 0) { DivergenceRms = MaximumDivergence = 0; return; }
        long started = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        PredictMomentum(seconds);
        long pressureStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        Project(seconds);
        long heatStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        TransportHeat(seconds);
        ReconstructVelocity(); PublishViews();
        if (ProfileSteps) LastTimings = new(Stopwatch.GetElapsedTime(started, pressureStart).TotalMilliseconds,
            Stopwatch.GetElapsedTime(pressureStart, heatStart).TotalMilliseconds, Stopwatch.GetElapsedTime(heatStart).TotalMilliseconds);
    }

    public void ExchangeSurface(World world, int x, int y, double seconds)
    {
        ref var body = ref world.At(x, y);
        Contact(ref body, x - 1, y, 1, 0, seconds); Contact(ref body, x + 1, y, -1, 0, seconds);
        Contact(ref body, x, y - 1, 0, 1, seconds); Contact(ref body, x, y + 1, 0, -1, seconds);
    }

    public void ExchangeParcel(ref Particle parcel, int x, int y, double conductance, double seconds)
    {
        int i = VolumeIndexAtFine(x, y);
        if (i < 0) return;
        ref var node = ref CollectionsMarshal.AsSpan(cells)[i];
        var air = new Particle { Element = Element.Air, Mass = node.Capacity / SpecificHeat,
            Energy = node.Capacity * AmbientTemperature + node.Heat };
        double before = parcel.Energy;
        Thermodynamics.Exchange(ref parcel, ref air, seconds, conductance);
        double q = before - parcel.Energy;
        node.Heat += q; Heat[node.Block] += q;
    }

    private void Contact(ref Particle body, int x, int y, int nx, int ny, double seconds)
    {
        if (x < 0 || x >= fineWidth || y < 0 || y >= fineHeight)
        {
            if (y >= fineHeight) return;
            double g = FineArea / (.5 * World.CellSize / LiquidProperties.Conductivity(body) + .5 * World.CellSize / Conductivity);
            BoundaryHeat -= Thermodynamics.ExchangeWithReservoir(ref body, AmbientTemperature, g, seconds);
            return;
        }
        int i = VolumeIndexAtFine(x, y);
        if (i < 0) return;
        // Interior material faces have no air contact. Resolve their material
        // temperature/conductivity only when there is an actual thermal path.
        double k = LiquidProperties.Conductivity(body);
        ref var node = ref CollectionsMarshal.AsSpan(cells)[i];
        double distance = nx != 0 ? Math.Abs(node.CenterX - (x + .5 + nx * .5) * World.CellSize)
            : Math.Abs(node.CenterY - (y + .5 + ny * .5) * World.CellSize);
        distance = Math.Max(World.CellSize * .5, distance);
        double conductance = FineArea / (.5 * World.CellSize / k + distance / Conductivity);
        var air = new Particle { Element = Element.Air, Mass = node.Capacity / SpecificHeat,
            Energy = node.Capacity * AmbientTemperature + node.Heat };
        double before = body.Energy;
        Thermodynamics.Exchange(ref body, ref air, seconds, conductance);
        double q = before - body.Energy;
        node.Heat += q; Heat[node.Block] += q;
    }
}
