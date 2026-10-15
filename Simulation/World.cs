namespace Powder;

public sealed partial class World
{
    public const double CellSize = .002; // 2 mm wide, rendered as exactly 2 × 2 pixels
    public const double Depth = .01;    // 1 cm slice perpendicular to the screen
    public const double CellVolume = CellSize * CellSize * Depth;
    public const double FixedDelta = 1.0 / 60;
    public int Width { get; }
    public int Height { get; }
    public Particle[] Cells { get; }
    public bool AmbientCooling { get; set; } = true;
    public double AmbientTemperature { get; set; } = 20;
    public long Tick { get; private set; }
    public double EscapedMass { get; private set; }
    public double ExternalEnergy { get; private set; }
    public int Count => Cells.Count(p => p.Element != Element.Empty);
    private Random random;
    private uint stamp;

    public World(int width, int height, int seed = 417)
    {
        if (width < 8 || height < 8) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        Cells = new Particle[width * height];
        random = new Random(seed);
    }

    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public ref Particle At(int x, int y) => ref Cells[y * Width + x];

    public void Set(int x, int y, Element e, double? temperature = null)
    {
        if (!Inside(x, y)) return;
        if (e == Element.Empty) { At(x, y) = default; return; }
        Material m = Materials.Get(e);
        double t = temperature ?? m.SpawnTemperature;
        double mass = m.Density * CellVolume;
        At(x, y) = new Particle
        {
            Element = e, MeltOrigin = Element.Stone, Mass = mass,
            Energy = mass * Thermodynamics.SpecificEnthalpy(e, t),
            Life = e == Element.Fire ? 1.1f + random.NextSingle() * 1.2f : 0,
            Variation = (byte)random.Next(256), Stamp = stamp
        };
    }

    public void Clear()
    {
        Array.Clear(Cells); Tick = 0; stamp = 0; EscapedMass = 0; ExternalEnergy = 0;
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

    public void HeatBrush(int x, int y, int radius, double degrees)
    {
        for (int yy = y - radius; yy <= y + radius; yy++)
            for (int xx = x - radius; xx <= x + radius; xx++)
                if (Inside(xx, yy) && (xx - x) * (xx - x) + (yy - y) * (yy - y) <= radius * radius)
                {
                    ref var p = ref At(xx, yy);
                    if (p.Element == Element.Empty) continue;
                    double q = degrees * p.HeatCapacity;
                    double minimum = Materials.IsWater(p.Element)
                        ? -273.15 * ((p.Mass - p.DissolvedSalt) * 2100 + p.DissolvedSalt * 880)
                        : p.Mass * Materials.Get(p.Element == Element.Lava ? p.MeltOrigin : p.Element).SpecificHeat * -273.15;
                    double maximum = p.Mass * (Materials.IsWater(p.Element)
                        ? Thermodynamics.SpecificEnthalpy(Element.Steam, 6000)
                        : Thermodynamics.CanMelt(p.Element) || p.Element == Element.Lava
                            ? Thermodynamics.LiquidEnthalpy(p.Element == Element.Lava ? p.MeltOrigin : p.Element, 6000)
                            : Materials.Get(p.Element).SpecificHeat * 6000);
                    double energy = Math.Clamp(p.Energy + q, minimum, maximum);
                    ExternalEnergy += energy - p.Energy; p.Energy = energy;
                    Thermodynamics.ResolvePhase(ref p);
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
        return (mass, energy, count);
    }

    public void Step()
    {
        stamp++; Tick++;
        // Pair exchanges are conservative; reverse the sweep to reduce directional bias.
        for (int k = 0; k < Cells.Length; k++)
        {
            int i = (Tick & 1) == 0 ? k : Cells.Length - 1 - k;
            if (Cells[i].Element == Element.Empty) continue;
            if (i % Width + 1 < Width) Thermodynamics.Exchange(ref Cells[i], ref Cells[i + 1], FixedDelta);
            if (i + Width < Cells.Length) Thermodynamics.Exchange(ref Cells[i], ref Cells[i + Width], FixedDelta);
            if (AmbientCooling && Exposed(i))
            {
                ref var p = ref Cells[i];
                double q = (AmbientTemperature - p.Temperature) * p.HeatCapacity
                    * (1 - Math.Exp(-12 * CellSize * Depth * FixedDelta / p.HeatCapacity));
                p.Energy += q; ExternalEnergy += q;
            }
        }
        // Bottom-up sweep plus stamps ensure a particle is moved at most once per tick.
        bool reverse = (Tick & 1) == 0;
        for (int y = Height - 1; y >= 0; y--)
            for (int k = 0; k < Width; k++)
            {
                int x = reverse ? Width - 1 - k : k;
                int i = y * Width + x;
                if (Cells[i].Element == Element.Empty || Cells[i].Stamp == stamp) continue;
                Cells[i].Stamp = stamp;
                React(i, x, y);
                Move(i, x, y);
            }
    }

    private bool Exposed(int i) => i < Width || i >= Cells.Length - Width || i % Width == 0
        || i % Width == Width - 1 || Cells[i - 1].Element == Element.Empty
        || Cells[i + 1].Element == Element.Empty || Cells[i - Width].Element == Element.Empty
        || Cells[i + Width].Element == Element.Empty;

    private void React(int i, int x, int y)
    {
        ref var p = ref Cells[i];
        Thermodynamics.ResolvePhase(ref p);
        double t = p.Temperature;
        if (p.Element is Element.Wood or Element.Oil && t > (p.Element == Element.Wood ? 300 : 280))
        {
            // Fuel energy is a deliberate chemical source. Oxygen is an ambient reservoir.
            double before = p.Energy;
            p.Element = Element.Fire;
            p.Energy = p.Mass * 1100 * Math.Max(t, 750);
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
    }

    private void Move(int i, int x, int y)
    {
        Element e = Cells[i].Element;
        MatterState state = Materials.Get(e).State;
        if (state is MatterState.Empty or MatterState.Solid) return;
        int direction = random.Next(2) == 0 ? -1 : 1;
        if (state is MatterState.Gas or MatterState.Flame)
        {
            // Buoyancy with bounded diffusion. The top boundary vents gases.
            if (y == 0)
            {
                EscapedMass += Cells[i].Mass; Cells[i] = default; return;
            }
            if (TryMove(i, x, y - 1, rising: true)) return;
            if (TryMove(i, x + direction, y - 1, rising: true)) return;
            if (random.Next(3) == 0) TryMove(i, x + direction, y, rising: true);
            return;
        }
        ref var p = ref Cells[i];
        p.VelocityY = Math.Min(240, p.VelocityY + (float)(9.81 / CellSize * FixedDelta));
        p.Travel += p.VelocityY * (float)FixedDelta;
        int steps = Math.Min(4, (int)p.Travel);
        p.Travel -= steps;
        int current = i, yy = y;
        for (int n = 0; n < steps; n++)
        {
            if (TryMove(current, x, yy + 1)) { yy++; current += Width; }
            else { Cells[current].VelocityY = 0; Cells[current].Travel = 0; break; }
        }
        if (current != i) return;
        if (TryMove(i, x + direction, y + 1) || TryMove(i, x - direction, y + 1)) return;
        if (state != MatterState.Liquid) return;
        // Lateral search gives water rapid leveling; viscous lava flows more slowly.
        if (e == Element.Lava && random.Next(3) != 0) return;
        int range = e == Element.Oil ? 3 : e == Element.Lava ? 1 : 5;
        for (int sign = 0; sign < 2; sign++)
        {
            int dir = sign == 0 ? direction : -direction;
            for (int distance = 1; distance <= range; distance++)
            {
                int xx = x + distance * dir;
                if (!Inside(xx, y) || At(xx, y).Element != Element.Empty) break;
                if (distance == range || (y + 1 < Height && At(xx, y + 1).Element == Element.Empty))
                {
                    TryMove(i, xx, y); return;
                }
            }
        }
    }

    private bool TryMove(int from, int x, int y, bool rising = false)
    {
        if (!Inside(x, y)) return false;
        int to = y * Width + x;
        Element target = Cells[to].Element;
        if (target != Element.Empty)
        {
            MatterState state = Materials.Get(target).State;
            if (state is MatterState.Solid or MatterState.Powder) return false;
            double a = Materials.Get(Cells[from].Element).Density;
            double b = Materials.Get(target).Density;
            // Density swaps only along gravity; horizontal swaps would unmix arbitrarily.
            if (y == from / Width || (rising ? a >= b : a <= b)) return false;
        }
        (Cells[from], Cells[to]) = (Cells[to], Cells[from]);
        Cells[from].Stamp = Cells[to].Stamp = stamp;
        return true;
    }
}
