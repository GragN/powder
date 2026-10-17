using Vec = nkast.Aether.Physics2D.Common.Vector2;

namespace Powder;

public sealed partial class RigidBodyField
{
    private int[] fluidRegions = [], columnTop = [];
    private readonly List<int> fluidQueue = [], fluidColumns = [], surfaceHeights = [];
    private readonly List<double> fluidSurfaces = [];

    private void PrepareFluidPressure()
    {
        if (fluidRegions.Length != world.Cells.Length)
        { fluidRegions = new int[world.Cells.Length]; columnTop = new int[world.Width]; Array.Fill(columnTop, int.MaxValue); }
        else Array.Clear(fluidRegions);
        fluidSurfaces.Clear(); fluidSurfaces.Add(0);
        for (int i = 0; i < world.Cells.Length; i++)
        {
            int kind = LiquidProperties.Kind(world.Cells[i]);
            if (kind == 0 || fluidRegions[i] != 0) continue;
            int region = fluidSurfaces.Count;
            fluidQueue.Clear(); fluidColumns.Clear(); surfaceHeights.Clear();
            fluidQueue.Add(i); fluidRegions[i] = region;
            for (int n = 0; n < fluidQueue.Count; n++)
            {
                int cell = fluidQueue[n], x = cell % world.Width, y = cell / world.Width;
                if (columnTop[x] == int.MaxValue) fluidColumns.Add(x);
                columnTop[x] = Math.Min(columnTop[x], y);
                for (int k = 0; k < 4; k++)
                {
                    int xx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), yy = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    int j = yy * world.Width + xx;
                    if (!world.Inside(xx, yy) || fluidRegions[j] != 0 || LiquidProperties.Kind(world.Cells[j]) != kind) continue;
                    fluidRegions[j] = region; fluidQueue.Add(j);
                }
            }
            // A median free-surface estimate avoids letting one splash redefine
            // the hydrostatic head of the entire connected liquid region.
            foreach (int x in fluidColumns) { surfaceHeights.Add(columnTop[x]); columnTop[x] = int.MaxValue; }
            surfaceHeights.Sort(); fluidSurfaces.Add(surfaceHeights[surfaceHeights.Count / 2]);
        }
    }

    private void ApplyFluidForces(Rigid rigid)
    {
        if (fluidSurfaces.Count <= 1) return;
        foreach (var face in rigid.Surface)
        {
            Vec point = rigid.Body.GetWorldPoint(face.Point), normal = rigid.Body.GetWorldVector(face.Normal);
            int cell = -1;
            for (int step = 0; step < 4; step++)
            {
                Vec outside = point + normal * ((.6f + .5f * step) * Unit);
                int x = (int)Math.Floor(outside.X / Unit), y = (int)Math.Floor(outside.Y / Unit);
                if (!world.Inside(x, y)) break;
                int j = y * world.Width + x;
                ref readonly var p = ref world.Cells[j];
                if (fluidRegions[j] > 0) { cell = j; break; }
                var state = Materials.Get(p.Element).State;
                if (state == MatterState.Powder || state == MatterState.Solid && p.RigidId != rigid.Id) break;
            }
            if (cell < 0) continue;
            ref readonly var liquid = ref world.Cells[cell];
            double depth = Math.Max(0, point.Y / Unit - fluidSurfaces[fluidRegions[cell]]) * World.CellSize;
            double area = World.CellSize * World.Depth, density = LiquidProperties.Density(liquid);
            double pressure = density * World.Gravity * depth;
            Vec velocity = rigid.Body.GetLinearVelocityFromWorldPoint(point);
            var flow = world.Liquids.VelocityAtFine(cell % world.Width, cell / world.Width);
            double vx = velocity.X * MetresPerUnit - flow.X, vy = velocity.Y * MetresPerUnit - flow.Y;
            double vn = vx * normal.X + vy * normal.Y;
            double normalForce = pressure + .5 * density * Math.Max(0, vn) * Math.Max(0, vn);
            double viscous = LiquidProperties.DynamicViscosity(liquid) * area / (World.CellSize * .5);
            double fx = -normal.X * normalForce * area - viscous * (vx - normal.X * vn);
            double fy = -normal.Y * normalForce * area - viscous * (vy - normal.Y * vn);
            rigid.Body.ApplyForce(new Vec((float)(fx / MetresPerUnit), (float)(fy / MetresPerUnit)), point);
        }
    }
}
