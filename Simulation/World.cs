using System.Diagnostics;

namespace Powder;

public sealed partial class World
{
    public const double CellSize = .002; // 2 mm wide, rendered as exactly 2 × 2 pixels
    public const double Depth = .01;    // 1 cm slice perpendicular to the screen
    public const double CellVolume = CellSize * CellSize * Depth;
    public const double FixedDelta = 1.0 / 60;
    public const double Gravity = 9.81; // m/s²; identical free-fall acceleration for every mass
    private const int ThermalSubsteps = 2;
    public int Width { get; }
    public int Height { get; }
    public Particle[] Cells { get; }
    public bool AtmosphereEnabled { get; set; } = true;
    public AirField Air { get; }
    public LiquidField Liquids { get; }
    public RigidBodyField Solids { get; }
    internal uint CurrentStamp => stamp;
    public long Tick { get; private set; }
    public long VisualRevision { get; private set; }
    private bool hadBodies;
    public double EscapedMass { get; private set; }
    public double EscapedEnergy { get; private set; }
    public double ExternalEnergy { get; private set; }
    public int Count => Statistics().Particles;
    private readonly int[] activeCells;
    private int activeCount;
    private Random random;
    private uint stamp;
    public bool ProfileSteps { get; set; }
    public readonly record struct Timings(double Thermal, double Liquid, double Motion, double Bubbles, double Atmosphere);
    public Timings LastTimings { get; private set; }

    public World(int width, int height, int seed = 417)
    {
        if (width < 8 || height < 8) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        Cells = new Particle[width * height];
        Bubbles = new Particle[Cells.Length]; bubbleScratch = new Particle[Cells.Length]; bubbleListed = new bool[Cells.Length];
        activeCells = new int[Cells.Length];
        Air = new AirField(width, height);
        Liquids = new LiquidField(width, height);
        Solids = new RigidBodyField(this);
        random = new Random(seed);
    }

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public ref Particle At(int x, int y) => ref Cells[y * Width + x];

    public void Set(int x, int y, Element e, double? temperature = null)
    {
        if (!Inside(x, y)) return;
        VisualRevision++;
        if (e == Element.Empty) { At(x, y) = default; Bubbles[y * Width + x] = default; return; }
        Material m = Materials.Get(e);
        double t = temperature ?? m.SpawnTemperature;
        double mass = m.Density * CellVolume;
        At(x, y) = new Particle
        {
            Element = e, MeltOrigin = e == Element.Oil || Thermodynamics.CanMelt(e) ? e : Element.Stone, Mass = mass,
            Energy = mass * Thermodynamics.SpecificEnthalpy(e, t),
            Life = e == Element.Fire ? 1.1f + random.NextSingle() * 1.2f : 0,
            Variation = (byte)random.Next(256), Stamp = stamp
        };
        Thermodynamics.ResolvePhase(ref At(x, y));
        Solids.Created(y * Width + x);
    }

    public void Clear()
    {
        VisualRevision++; hadBodies = false;
        Array.Clear(Cells); Array.Clear(Bubbles); Array.Clear(bubbleScratch);
        bubbleIndices.Clear(); nextBubbleIndices.Clear(); Array.Clear(bubbleListed); activeCount = 0;
        Air.Reset(); Liquids.Reset(); Tick = 0; stamp = 0; EscapedMass = 0; EscapedEnergy = 0; ExternalEnergy = 0;
        Solids.Reset();
        random = new Random(417);
    }

    public void Brush(int x, int y, int radius, Element element, bool replace = false)
    {
        for (int yy = y - radius; yy <= y + radius; yy++)
            for (int xx = x - radius; xx <= x + radius; xx++)
                if (Inside(xx, yy) && (xx - x) * (xx - x) + (yy - y) * (yy - y) <= radius * radius
                    && (replace || element == Element.Empty || At(xx, yy).Element == Element.Empty))
                    Set(xx, yy, element);
    }

    // Each covered cell receives the same power, not the same temperature change.
    public void HeatBrush(int x, int y, int radius, double powerWatts, double seconds = FixedDelta)
    {
        VisualRevision++;
        if (!double.IsFinite(powerWatts) || !double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(powerWatts));
        for (int yy = y - radius; yy <= y + radius; yy++)
            for (int xx = x - radius; xx <= x + radius; xx++)
                if (Inside(xx, yy) && (xx - x) * (xx - x) + (yy - y) * (yy - y) <= radius * radius)
                {
                    ref var p = ref At(xx, yy);
                    if (p.Element == Element.Empty) continue;
                    double energy = Math.Clamp(p.Energy + powerWatts * seconds,
                        Thermodynamics.EnergyAtTemperature(p, -273.15), Thermodynamics.EnergyAtTemperature(p, 6000));
                    ExternalEnergy += energy - p.Energy; p.Energy = energy;
                    Thermodynamics.ResolvePhase(ref p);
                    if (p.RigidId == -2) Solids.QueueSolid(yy * Width + xx);
                }
    }

    public (double Mass, double Energy, int Particles) Statistics()
    {
        double mass = 0, energy = 0; int count = 0;
        foreach (ref readonly var p in Cells.AsSpan())
        {
            mass += p.Mass; energy += p.Energy;
            if (p.Element != Element.Empty) count++;
        }
        foreach (int i in bubbleIndices)
        {
            ref readonly var p = ref Bubbles[i];
            mass += p.Mass; energy += p.Energy;
            if (p.Element != Element.Empty) count++;
        }
        return (mass, energy, count);
    }

    public void Step()
    {
        Air.ProfileSteps = Liquids.ProfileSteps = ProfileSteps;
        long profiling = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        stamp++; Tick++;
        activeCount = 0;
        for (int i = 0; i < Cells.Length; i++) if (Cells[i].Element != Element.Empty) activeCells[activeCount++] = i;
        if (AtmosphereEnabled) Air.UpdateObstacles(this);
        if (activeCount == 0 && bubbleIndices.Count == 0)
        {
            if (hadBodies) { Liquids.Reset(); hadBodies = false; }
            if (AtmosphereEnabled) Air.Step(FixedDelta);
            if (AtmosphereEnabled && Air.Active) VisualRevision++;
            if (ProfileSteps) LastTimings = new(0, 0, 0, 0, Stopwatch.GetElapsedTime(profiling).TotalMilliseconds);
            return;
        }
        hadBodies = true; VisualRevision++;
        // Two smaller, alternating sweeps reduce splitting error without a stability cap on k.
        for (int substep = 0; substep < ThermalSubsteps; substep++)
        {
            double seconds = FixedDelta / ThermalSubsteps;
            for (int k = 0; k < activeCount; k++)
            {
                int i = activeCells[((Tick + substep) & 1) == 0 ? k : activeCount - 1 - k];
                if (Cells[i].Element == Element.Empty) continue;
                if (i % Width + 1 < Width) Thermodynamics.Exchange(ref Cells[i], ref Cells[i + 1], seconds);
                if (i + Width < Cells.Length) Thermodynamics.Exchange(ref Cells[i], ref Cells[i + Width], seconds);
                if (AtmosphereEnabled && ExposedToGas(i)) Air.ExchangeSurface(this, i % Width, i / Width, seconds);
            }
        }
        double thermalMs = ProfileSteps ? Stopwatch.GetElapsedTime(profiling).TotalMilliseconds : 0;
        long phaseStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        ReleaseVapor();
        long liquidStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        Liquids.Step(this, FixedDelta);
        double liquidMs = ProfileSteps ? Stopwatch.GetElapsedTime(liquidStart).TotalMilliseconds : 0;
        long motionStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        // Visit occupied rows bottom-up, alternating horizontal order. New parcels
        // have the current stamp and cannot acquire a second timestep this frame.
        bool reverse = (Tick & 1) == 0;
        for (int end = activeCount - 1; end >= 0;)
        {
            int y = activeCells[end] / Width, start = end;
            while (start > 0 && activeCells[start - 1] / Width == y) start--;
            for (int k = start; k <= end; k++)
            {
                int i = activeCells[reverse ? end - k + start : k];
                if (Cells[i].Element == Element.Empty || Cells[i].Stamp == stamp) continue;
                Cells[i].Stamp = stamp;
                React(i, i % Width, y);
                if (Cells[i].RigidId == -2) Solids.QueueSolid(i);
                Move(i, i % Width, y);
            }
            end = start - 1;
        }
        double motionMs = ProfileSteps ? Stopwatch.GetElapsedTime(motionStart).TotalMilliseconds : 0;
        long bubblesStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        if (bubbleIndices.Count != 0) StepBubbles();
        double bubblesMs = ProfileSteps ? Stopwatch.GetElapsedTime(bubblesStart).TotalMilliseconds : 0;
        double releaseMs = ProfileSteps ? Stopwatch.GetElapsedTime(phaseStart, liquidStart).TotalMilliseconds : 0;
        long rigidStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        Solids.Step(FixedDelta);
        if (ProfileSteps) motionMs += Stopwatch.GetElapsedTime(rigidStart).TotalMilliseconds;
        long atmosphereStart = ProfileSteps ? Stopwatch.GetTimestamp() : 0;
        if (AtmosphereEnabled)
        {
            Air.UpdateObstacles(this);
            Air.Step(FixedDelta);
        }
        if (ProfileSteps) LastTimings = new(thermalMs, liquidMs, motionMs, bubblesMs + releaseMs,
            Stopwatch.GetElapsedTime(atmosphereStart).TotalMilliseconds);
    }

    private bool ExposedToGas(int i)
    {
        bool Gas(int j) => Cells[j].Element == Element.Empty
            || Materials.Get(Cells[j].Element).State is MatterState.Gas or MatterState.Flame;
        int x = i % Width;
        return x == 0 || x == Width - 1 || i < Width || Gas(i - 1) || Gas(i + 1)
            || Gas(i - Width) || i + Width < Cells.Length && Gas(i + Width);
    }

    private void React(int i, int x, int y)
    {
        ref var p = ref Cells[i];
        Thermodynamics.ResolvePhase(ref p);
        double t = p.Temperature;
        if (p.Element == Element.Wood && t > 300
            || p.Element == Element.Vapor && p.MeltOrigin == Element.Oil && t > 400)
        {
            // Fuel energy is a deliberate chemical source. Oxygen is an ambient reservoir.
            double before = p.Energy;
            p.Element = Element.Fire;
            p.Energy = Math.Max(before, p.Mass * 1100 * Math.Max(t, 750));
            ExternalEnergy += p.Energy - before;
            p.Life = 1.5f + random.NextSingle() * 2;
        }
        else if (p.Element == Element.Fire)
        {
            p.Life -= (float)FixedDelta;
            double q = p.Mass * 18_000_000 * FixedDelta / 4;
            if (p.Temperature < 1250) { p.Energy += q; ExternalEnergy += q; }
            if (p.Life <= 0)
            {
                // The exhaust retains energy; thermal cooling then exchanges it with air.
                p.Element = Element.Smoke;
            }
        }
        else if (p.Element == Element.Salt)
        {
            int nx = Math.Clamp(x + random.Next(-1, 2), 0, Width - 1);
            int ny = Math.Clamp(y + random.Next(-1, 2), 0, Height - 1);
            ref var other = ref At(nx, ny);
            if (other.Element == Element.Water && other.DissolvedSalt < other.Mass * .26)
            {
                double limit = Math.Max(0, (other.Mass - other.DissolvedSalt) * .35 - other.DissolvedSalt);
                double amount = Math.Min(p.Mass, Math.Min(limit, p.Mass * .04));
                double energy = p.Energy * amount / p.Mass;
                other.DissolvedSalt += amount; other.Mass += amount; other.Energy += energy;
                p.Mass -= amount; p.Energy -= energy;
                if (p.Mass < 1e-12) { other.Mass += p.Mass; other.Energy += p.Energy; p = default; }
            }
        }
        if (p.RigidId != 0 && Materials.Get(p.Element).State != MatterState.Solid) p.RigidId = 0;
    }

    private void Escape(int index)
    {
        EscapedMass += Cells[index].Mass; EscapedEnergy += Cells[index].Energy;
        Cells[index] = default;
    }

    internal void RigidEscape(in Particle p) { EscapedMass += p.Mass; EscapedEnergy += p.Energy; }
    internal void PlaceRigidBubble(int index, in Particle p) => AddBubble(index, p);

    private bool TryMove(int from, int x, int y, bool rising = false)
    {
        if (y >= Height) return false;
        if (x < 0 || x >= Width || y < 0) { Escape(from); return true; }
        int to = y * Width + x;
        Element target = Cells[to].Element;
        if (target != Element.Empty)
        {
            MatterState state = Materials.Get(target).State;
            if (state is MatterState.Solid or MatterState.Powder) return false;
            // Same-fluid bulk motion and heat advection are resolved by the continuum solver.
            // Whole-cell swaps are reserved for density separation at material interfaces.
            int sourceKind = LiquidProperties.Kind(Cells[from]);
            if (sourceKind != 0 && sourceKind == LiquidProperties.Kind(Cells[to])) return false;
            double a = LiquidProperties.Density(Cells[from]);
            double b = LiquidProperties.Density(Cells[to]);
            // Density swaps only along gravity; horizontal swaps would unmix arbitrarily.
            if (y == from / Width || (rising ? a >= b : a <= b)) return false;
        }
        (Cells[from], Cells[to]) = (Cells[to], Cells[from]);
        Cells[from].Stamp = Cells[to].Stamp = stamp;
        return true;
    }
}
