using Vec = nkast.Aether.Physics2D.Common.Vector2;

namespace Powder;

public sealed partial class RigidBodyField
{
    private readonly int[] rasterMarks, vacancyNext, vacancyHeads;
    private int rasterGeneration;

    public void Step(double seconds)
    {
        Synchronize();
        if (bodies.Count == 0 || seconds <= 0) return;
        UpdateTerrain();
        work.Clear(); work.AddRange(bodies.Values);
        PrepareFluidPressure();
        double speed = 0, spin = 0;
        foreach (var r in work)
        {
            r.PreviousPosition = r.Body.Position; r.PreviousAngle = r.Body.Rotation;
            speed = Math.Max(speed, r.Body.LinearVelocity.Length() + Math.Abs(r.Body.AngularVelocity) * r.Radius);
            spin = Math.Max(spin, Math.Abs(r.Body.AngularVelocity));
        }
        // Millimetre objects use smaller contact steps. Keep the library's
        // translation/rotation guards inactive. Limit the travel of the fastest
        // surface point to half a particle, including this frame's acceleration.
        double travel = (speed + World.Gravity / MetresPerUnit * seconds) * seconds;
        int steps = Math.Max(16, (int)Math.Ceiling(Math.Max(travel / (Unit * .5), spin * seconds / .25)));
        float dt = (float)(seconds / steps);
        for (int n = 0; n < steps; n++)
        {
            foreach (var r in work) ApplyFluidForces(r);
            engine!.Step(dt);
        }
        Rasterize();
    }

    private void Rasterize()
    {
        foreach (var r in work) r.RasterFrozen = false;
        bool retry;
        do
        {
            retry = false;
            if (++rasterGeneration == int.MaxValue) { Array.Clear(rasterMarks); rasterGeneration = 1; }
            foreach (var r in work) if (r.RasterFrozen) foreach (var s in r.Samples)
            { s.Target = s.Cell; rasterMarks[s.Cell] = rasterGeneration; }
            foreach (var r in work)
            {
                if (r.RasterFrozen) continue;
                if (!RasterPiece(r))
                {
                    // A tightly packed continuous shape can have no valid pixel
                    // layout. Restrict only this body, not unrelated falling pieces.
                    r.Body.SetTransform(r.PreviousPosition, r.PreviousAngle);
                    r.Body.LinearVelocity = Vec.Zero; r.Body.AngularVelocity = 0;
                    r.RasterFrozen = true; retry = true;
                }
                if (retry) break;
            }
        } while (retry);
        foreach (var r in work) foreach (var s in r.Samples) world.Cells[s.Cell] = default;
        Array.Fill(vacancyHeads, -1);
        foreach (var r in work) foreach (var s in r.Samples)
        {
            if (rasterMarks[s.Cell] == rasterGeneration) continue;
            int x = s.Cell % world.Width;
            vacancyNext[s.Cell] = vacancyHeads[x]; vacancyHeads[x] = s.Cell;
        }
        bool clipped = false, bubblesMoved = false;
        foreach (var r in work) foreach (var s in r.Samples)
        {
            if (s.Target < 0) { world.RigidEscape(s.Value); s.Cell = -1; clipped = true; continue; }
            ref var target = ref world.Cells[s.Target];
            int displacedTo = -1;
            if (target.Element != Element.Empty)
            {
                displacedTo = TakeVacancy(s.Target);
                if (displacedTo < 0) throw new InvalidOperationException("Rigid displacement has no vacated volume.");
                world.Cells[displacedTo] = target; world.Cells[displacedTo].Stamp = world.CurrentStamp;
            }
            Vec velocity = r.Body.GetLinearVelocityFromLocalPoint(new Vec((s.X + .5f) * Unit, (s.Y + .5f) * Unit));
            s.Value.VelocityX = velocity.X / Unit; s.Value.VelocityY = velocity.Y / Unit;
            s.Value.TravelX = s.Value.Travel = 0; s.Value.Stamp = world.CurrentStamp;
            target = s.Value; s.Cell = s.Target;
            if (world.Bubbles[s.Cell].Element != Element.Empty)
            {
                int to = displacedTo >= 0 && world.Bubbles[displacedTo].Element == Element.Empty ? displacedTo : BubbleHome(s.Cell);
                if (to >= 0)
                {
                    var bubble = world.Bubbles[s.Cell]; world.Bubbles[s.Cell] = default;
                    world.PlaceRigidBubble(to, bubble); bubblesMoved = true;
                }
            }
        }
        if (bubblesMoved) world.RebuildBubbleIndices();
        if (clipped) Synchronize();
    }

    private int TakeVacancy(int near)
    {
        int x0 = near % world.Width, y0 = near / world.Width;
        for (int dx = 0; dx < world.Width; dx++) for (int side = 0; side < (dx == 0 ? 1 : 2); side++)
        {
            int x = x0 + (side == 0 ? dx : -dx);
            if (x < 0 || x >= world.Width) continue;
            int best = -1, previous = -1, bestPrevious = -1, distance = int.MaxValue;
            for (int i = vacancyHeads[x]; i >= 0; previous = i, i = vacancyNext[i])
            {
                int d = Math.Abs(i / world.Width - y0);
                if (d < distance) { best = i; bestPrevious = previous; distance = d; }
            }
            if (best < 0) continue;
            if (bestPrevious < 0) vacancyHeads[x] = vacancyNext[best]; else vacancyNext[bestPrevious] = vacancyNext[best];
            return best;
        }
        return -1;
    }

    private int BubbleHome(int near)
    {
        int cx = near % world.Width, cy = near / world.Width;
        for (int radius = 1; radius < Math.Max(world.Width, world.Height); radius++)
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(world.Height - 1, cy + radius); y++)
        for (int x = Math.Max(0, cx - radius); x <= Math.Min(world.Width - 1, cx + radius); x++)
        {
            if (Math.Abs(x - cx) != radius && Math.Abs(y - cy) != radius) continue;
            int i = y * world.Width + x;
            if (rasterMarks[i] == rasterGeneration || world.Bubbles[i].Element != Element.Empty) continue;
            if (Materials.Get(world.Cells[i].Element).State is MatterState.Solid or MatterState.Powder) continue;
            return i;
        }
        return -1;
    }
}
