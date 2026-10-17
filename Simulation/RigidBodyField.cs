using PhysicsBody = nkast.Aether.Physics2D.Dynamics.Body;
using PhysicsWorld = nkast.Aether.Physics2D.Dynamics.World;
using Vec = nkast.Aether.Physics2D.Common.Vector2;
using nkast.Aether.Physics2D.Dynamics;

namespace Powder;

// The contact solver works in 0.1 units per particle. Mass remains kilograms;
// all public diagnostics/save data use metres, seconds and radians.
public sealed partial class RigidBodyField
{
    private const float Unit = .1f;
    // Aether skips polygon centroids at zero shape density. Positive geometry
    // density is needed even for static fixtures; real mass/inertia are set below.
    private const float GeometryDensity = 1;
    private const double MetresPerUnit = World.CellSize / Unit;
    private readonly World world;
    private PhysicsWorld? engine;
    private readonly Dictionary<int, Rigid> bodies = [];
    private readonly List<Rigid> work = [];
    private readonly List<int> draft = [], pending = [];
    private readonly bool[] pendingListed;
    private bool anchoredStroke;
    private int nextId = 1;
    public bool Drawing { get; private set; }
    public int Count => bodies.Count;

    private sealed class Sample
    {
        public int X, Y, Cell, Target;
        public double DesiredX, DesiredY;
        public int MatchStamp;
        public Sample? MatchParent;
        public Particle Value;
    }

    private sealed class Rigid
    {
        public required int Id;
        public required PhysicsBody Body;
        public required List<Sample> Samples;
        public required List<SurfaceFace> Surface;
        public required HashSet<(int, int)> Sites;
        public Vec PreviousPosition;
        public float PreviousAngle;
        public bool RasterFrozen;
        public float Radius;
    }

    private readonly record struct SurfaceFace(Vec Point, Vec Normal);

    public readonly record struct BodyInfo(int Id, int Particles, double Mass, double Inertia,
        double X, double Y, double VelocityX, double VelocityY, double Angle, double AngularVelocity, bool Sleeping);

    public RigidBodyField(World owner)
    {
        world = owner;
        pendingListed = new bool[owner.Cells.Length];
        terrain = new Element[owner.Cells.Length];
        chunksX = (owner.Width + Chunk - 1) / Chunk;
        terrainBodies = new PhysicsBody?[chunksX * ((owner.Height + Chunk - 1) / Chunk)];
        dirtyChunks = new bool[terrainBodies.Length];
        rasterMarks = new int[owner.Cells.Length]; vacancyNext = new int[owner.Cells.Length];
        vacancyHeads = new int[owner.Width];
    }

    public BodyInfo[] Diagnostics() => bodies.Values.Select(r =>
    {
        double mass = r.Samples.Sum(s => s.Value.Mass);
        var b = r.Body;
        double inertia = b.Inertia - b.Mass * (b.LocalCenter.X * b.LocalCenter.X + b.LocalCenter.Y * b.LocalCenter.Y);
        return new BodyInfo(r.Id, r.Samples.Count, mass, inertia * MetresPerUnit * MetresPerUnit,
            b.WorldCenter.X * MetresPerUnit, b.WorldCenter.Y * MetresPerUnit,
            b.LinearVelocity.X * MetresPerUnit, b.LinearVelocity.Y * MetresPerUnit,
            b.Rotation, b.AngularVelocity, !b.Awake);
    }).ToArray();

    public void SetMotion(int id, double vx, double vy, double angularVelocity = 0)
    {
        if (!bodies.TryGetValue(id, out var rigid) || !double.IsFinite(vx) || !double.IsFinite(vy)
            || !double.IsFinite(angularVelocity) || Math.Abs(vx) > 1000 || Math.Abs(vy) > 1000
            || Math.Abs(angularVelocity) > 10_000) throw new ArgumentException("Invalid rigid motion.");
        rigid.Body.LinearVelocity = new((float)(vx / MetresPerUnit), (float)(vy / MetresPerUnit));
        rigid.Body.AngularVelocity = (float)angularVelocity;
        rigid.Body.Awake = true;
    }

    public void BeginStroke(bool anchored = false)
    {
        if (Drawing) EndStroke();
        Drawing = true; anchoredStroke = anchored;
    }

    internal void Created(int index)
    {
        ref var p = ref world.Cells[index];
        if (Drawing && IsSolid(p)) { p.RigidId = -1; draft.Add(index); }
        else if (p.RigidId == -2) QueueSolid(index);
    }

    internal void QueueSolid(int index)
    {
        if (pendingListed[index]) return;
        pendingListed[index] = true; pending.Add(index);
    }

    public void EndStroke()
    {
        if (!Drawing) return;
        Drawing = false;
        foreach (int i in draft)
        {
            ref var p = ref world.Cells[i];
            if (p.RigidId != -1) continue;
            p.RigidId = anchoredStroke || !IsSolid(p) ? 0 : -2;
            if (p.RigidId == -2) QueueSolid(i);
        }
        draft.Clear();
        Synchronize();
    }

    public void Reset()
    {
        bodies.Clear(); work.Clear(); draft.Clear(); pending.Clear(); Array.Clear(pendingListed);
        engine = null; Drawing = false; nextId = 1; Array.Clear(terrain); Array.Clear(terrainBodies); Array.Clear(dirtyChunks);
        pixelOwners.Clear(); selectedPixels.Clear(); matchQueue.Clear(); pixelCandidates.Clear();
    }

    private static bool IsSolid(in Particle p) => p.Element != Element.Empty && Materials.Get(p.Element).State == MatterState.Solid;

    private void EnsureEngine()
    {
        if (engine is not null) return;
        engine = new PhysicsWorld(new Vec(0, (float)(World.Gravity / MetresPerUnit)));
        var floor = engine.CreateBody(new Vec(world.Width * Unit / 2, (world.Height + 2) * Unit));
        var fixture = floor.CreateRectangle(world.Width * Unit, 4 * Unit, GeometryDensity, Vec.Zero);
        fixture.Friction = .65f; fixture.Restitution = .03f;
    }

    public void Synchronize()
    {
        work.Clear(); work.AddRange(bodies.Values);
        foreach (var rigid in work)
        {
            bool changed = false;
            foreach (var s in rigid.Samples)
            {
                if (s.Cell < 0) { changed = true; continue; }
                ref var p = ref world.Cells[s.Cell];
                if (p.RigidId != rigid.Id || !IsSolid(p))
                {
                    if (p.RigidId == rigid.Id) p.RigidId = 0;
                    changed = true; continue;
                }
                changed |= p.Element != s.Value.Element || p.Mass != s.Value.Mass;
                s.Value = p;
            }
            if (changed) RebuildFragments(rigid);
        }
        if (pending.Count == 0) return;
        var candidates = new HashSet<int>();
        foreach (int i in pending)
        {
            pendingListed[i] = false;
            if (world.Cells[i].RigidId == -2 && IsSolid(world.Cells[i])) candidates.Add(i);
        }
        pending.Clear();
        while (candidates.Count > 0)
        {
            int first = candidates.First();
            var connected = new List<int> { first }; candidates.Remove(first);
            for (int n = 0; n < connected.Count; n++)
            {
                int i = connected[n], x = i % world.Width, y = i / world.Width;
                for (int k = 0; k < 4; k++)
                {
                    int xx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), yy = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    int j = yy * world.Width + xx;
                    if (world.Inside(xx, yy) && candidates.Remove(j)) connected.Add(j);
                }
            }
            int x0 = connected.Min(i => i % world.Width), y0 = connected.Min(i => i / world.Width);
            var samples = connected.Select(i => new Sample { X = i % world.Width - x0, Y = i / world.Width - y0,
                Cell = i, Value = world.Cells[i] }).ToList();
            double mass = samples.Sum(s => s.Value.Mass);
            var velocity = new Vec((float)(samples.Sum(s => s.Value.Mass * s.Value.VelocityX) / mass * Unit),
                (float)(samples.Sum(s => s.Value.Mass * s.Value.VelocityY) / mass * Unit));
            Create(samples, new Vec(x0 * Unit, y0 * Unit), 0, velocity, 0, nextId++);
        }
    }

    private Rigid Create(List<Sample> samples, Vec position, float angle, Vec velocity, float spin, int id)
    {
        EnsureEngine();
        var body = engine!.CreateBody(position, angle, BodyType.Dynamic);
        body.IsBullet = true;
        var remaining = samples.ToDictionary(s => (s.X, s.Y));
        foreach (var sample in samples.OrderBy(s => s.Y).ThenBy(s => s.X))
        {
            if (!remaining.ContainsKey((sample.X, sample.Y))) continue;
            int width = 1, height = 1;
            bool Same(int x, int y) => remaining.TryGetValue((x, y), out var s) && s.Value.Element == sample.Value.Element;
            while (Same(sample.X + width, sample.Y)) width++;
            while (true)
            {
                bool row = true;
                for (int x = 0; x < width; x++) if (!Same(sample.X + x, sample.Y + height)) { row = false; break; }
                if (!row) break; height++;
            }
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) remaining.Remove((sample.X + x, sample.Y + y));
            var fixture = body.CreateRectangle(width * Unit, height * Unit, GeometryDensity,
                new Vec((sample.X + width * .5f) * Unit, (sample.Y + height * .5f) * Unit));
            fixture.Friction = Friction(sample.Value.Element); fixture.Restitution = .05f;
        }
        double mass = 0, cx = 0, cy = 0, inertia = 0;
        foreach (var sample in samples)
        {
            double m = sample.Value.Mass, x = (sample.X + .5) * Unit, y = (sample.Y + .5) * Unit;
            mass += m; cx += m * x; cy += m * y; inertia += m * (x * x + y * y + Unit * Unit / 6);
            sample.Value.RigidId = id; world.Cells[sample.Cell].RigidId = id;
        }
        body.Mass = (float)mass; body.LocalCenter = new Vec((float)(cx / mass), (float)(cy / mass));
        body.Inertia = (float)inertia; body.LinearVelocity = velocity; body.AngularVelocity = spin;
        var sites = samples.Select(s => (s.X, s.Y)).ToHashSet();
        var surface = new List<SurfaceFace>();
        foreach (var s in samples) for (int k = 0; k < 4; k++)
        {
            int nx = k == 0 ? -1 : k == 1 ? 1 : 0, ny = k == 2 ? -1 : k == 3 ? 1 : 0;
            if (sites.Contains((s.X + nx, s.Y + ny))) continue;
            surface.Add(new(new Vec((s.X + .5f + nx * .5f) * Unit, (s.Y + .5f + ny * .5f) * Unit), new Vec(nx, ny)));
        }
        float radius = (float)samples.Max(s => Math.Sqrt(Math.Pow((s.X + .5) * Unit - cx / mass, 2)
            + Math.Pow((s.Y + .5) * Unit - cy / mass, 2)) + Unit * Math.Sqrt(.5));
        var rigid = new Rigid { Id = id, Body = body, Samples = samples, Surface = surface, Radius = radius, Sites = sites };
        bodies.Add(id, rigid);
        return rigid;
    }

    private void RebuildFragments(Rigid original)
    {
        var b = original.Body;
        float angle = b.Rotation, spin = b.AngularVelocity;
        var oldCenter = b.WorldCenter; var velocity = b.LinearVelocity;
        var remaining = original.Samples.Where(s => s.Cell >= 0 && world.Cells[s.Cell].RigidId == original.Id
            && IsSolid(world.Cells[s.Cell])).ToDictionary(s => (s.X, s.Y));
        engine!.Remove(b); bodies.Remove(original.Id);
        bool first = true;
        while (remaining.Count > 0)
        {
            var sample = remaining.First().Value;
            var group = new List<Sample> { sample }; remaining.Remove((sample.X, sample.Y));
            for (int n = 0; n < group.Count; n++) for (int k = 0; k < 4; k++)
            {
                var s = group[n]; var key = (s.X + (k == 0 ? -1 : k == 1 ? 1 : 0), s.Y + (k == 2 ? -1 : k == 3 ? 1 : 0));
                if (remaining.Remove(key, out var neighbour)) group.Add(neighbour);
            }
            int id = first ? original.Id : nextId++; first = false;
            int ox = group.Min(s => s.X), oy = group.Min(s => s.Y);
            Vec origin = b.GetWorldPoint(new Vec(ox * Unit, oy * Unit));
            foreach (var s in group) { s.X -= ox; s.Y -= oy; }
            var child = Create(group, origin, angle, velocity, spin, id);
            Vec offset = child.Body.WorldCenter - oldCenter;
            child.Body.LinearVelocity = velocity + new Vec(-spin * offset.Y, spin * offset.X);
        }
    }

    private static float Friction(Element e) => e switch
    { Element.Ice => .03f, Element.Glass => .28f, Element.Wood => .55f, Element.Stone => .7f, _ => .45f };
}
