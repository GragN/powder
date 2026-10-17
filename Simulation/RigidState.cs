using Vec = nkast.Aether.Physics2D.Common.Vector2;

namespace Powder;

public sealed partial class RigidBodyField
{
    public sealed record SampleState(int Cell, int X, int Y);
    public sealed record BodyState(int Id, double X, double Y, double Angle, double Vx, double Vy,
        double Spin, bool Sleeping, SampleState[] Samples);
    public sealed record State(int NextId, BodyState[] Bodies);

    public State Capture()
    {
        for (int i = 0; i < world.Cells.Length; i++) if (world.Cells[i].RigidId == -2) QueueSolid(i);
        EndStroke(); Synchronize();
        return new(nextId, bodies.Values.Select(r => new BodyState(r.Id,
            r.Body.Position.X * MetresPerUnit, r.Body.Position.Y * MetresPerUnit,
            r.Body.Rotation, r.Body.LinearVelocity.X * MetresPerUnit, r.Body.LinearVelocity.Y * MetresPerUnit,
            r.Body.AngularVelocity, !r.Body.Awake, r.Samples.Select(s => new SampleState(s.Cell, s.X, s.Y)).ToArray())).ToArray());
    }

    public void Restore(State state)
    {
        if (state is null || state.NextId <= 0 || state.NextId > 2_000_000 || state.Bodies is null
            || state.Bodies.Length > world.Cells.Length) throw new InvalidDataException("Invalid rigid body data.");
        var ids = new HashSet<int>(); var occupied = new HashSet<int>();
        foreach (var body in state.Bodies)
        {
            if (body is null || body.Id <= 0 || body.Id >= state.NextId || !ids.Add(body.Id)
                || !double.IsFinite(body.X) || !double.IsFinite(body.Y)
                || Math.Abs(body.X) > 4 * world.Width * World.CellSize || Math.Abs(body.Y) > 4 * world.Height * World.CellSize
                || !double.IsFinite(body.Angle) || Math.Abs(body.Angle) > 1_000_000
                || !double.IsFinite(body.Vx) || !double.IsFinite(body.Vy) || Math.Abs(body.Vx) > 1000 || Math.Abs(body.Vy) > 1000
                || !double.IsFinite(body.Spin) || Math.Abs(body.Spin) > 10_000
                || body.Samples is null || body.Samples.Length == 0 || body.Samples.Length > world.Cells.Length)
                throw new InvalidDataException("Invalid rigid body state.");
            var sites = new HashSet<(int, int)>();
            foreach (var sample in body.Samples)
            {
                if (sample is null || sample.Cell < 0 || sample.Cell >= world.Cells.Length || !occupied.Add(sample.Cell)
                    || sample.X < 0 || sample.X >= world.Width || sample.Y < 0 || sample.Y >= world.Height
                    || !sites.Add((sample.X, sample.Y)) || world.Cells[sample.Cell].RigidId != body.Id || !IsSolid(world.Cells[sample.Cell]))
                    throw new InvalidDataException("Invalid rigid particle membership.");
                double x = body.X / World.CellSize + Math.Cos(body.Angle) * (sample.X + .5) - Math.Sin(body.Angle) * (sample.Y + .5);
                double y = body.Y / World.CellSize + Math.Sin(body.Angle) * (sample.X + .5) + Math.Cos(body.Angle) * (sample.Y + .5);
                if (Math.Abs(x - (sample.Cell % world.Width + .5)) > 4.51 || Math.Abs(y - (sample.Cell / world.Width + .5)) > 4.51)
                    throw new InvalidDataException("Rigid pose does not match its particles.");
            }
        }
        for (int i = 0; i < world.Cells.Length; i++)
        {
            if (world.Cells[i].RigidId < 0 || world.Cells[i].RigidId > 0 && !occupied.Contains(i))
                throw new InvalidDataException("Unowned rigid particle.");
        }
        Reset(); nextId = state.NextId;
        foreach (var body in state.Bodies)
        {
            var samples = body.Samples.Select(s => new Sample { Cell = s.Cell, X = s.X, Y = s.Y, Value = world.Cells[s.Cell] }).ToList();
            var rigid = Create(samples, new Vec((float)(body.X / MetresPerUnit), (float)(body.Y / MetresPerUnit)),
                (float)body.Angle, new Vec((float)(body.Vx / MetresPerUnit), (float)(body.Vy / MetresPerUnit)), (float)body.Spin, body.Id);
            rigid.Body.Awake = !body.Sleeping;
        }
    }
}
