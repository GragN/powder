using Vec = nkast.Aether.Physics2D.Common.Vector2;

namespace Powder;

public sealed partial class RigidBodyField
{
    private readonly record struct PixelCandidate(int Cell, int Coverage, double Error);
    private readonly List<PixelCandidate> pixelCandidates = [];
    private readonly HashSet<int> selectedPixels = [];
    private readonly Dictionary<int, Sample> pixelOwners = [];
    private readonly List<Sample> matchQueue = [];
    private int matchGeneration;

    private bool CanOccupy(int cell)
    {
        if (rasterMarks[cell] == rasterGeneration) return false;
        ref readonly var p = ref world.Cells[cell];
        var state = Materials.Get(p.Element).State;
        return state != MatterState.Powder && (state != MatterState.Solid || p.RigidId > 0);
    }

    private bool RasterPiece(Rigid rigid)
    {
        pixelCandidates.Clear(); selectedPixels.Clear(); pixelOwners.Clear(); matchQueue.Clear();
        double minX = world.Width, minY = world.Height, maxX = 0, maxY = 0;
        int count = 0;
        foreach (var s in rigid.Samples)
        {
            Vec point = rigid.Body.GetWorldPoint(new Vec((s.X + .5f) * Unit, (s.Y + .5f) * Unit));
            s.DesiredX = point.X / Unit; s.DesiredY = Math.Min(world.Height - .001, point.Y / Unit);
            if (s.DesiredX < 0 || s.DesiredX >= world.Width || s.DesiredY < 0) { s.Target = -1; continue; }
            s.Target = -2; count++;
            minX = Math.Min(minX, s.DesiredX); minY = Math.Min(minY, s.DesiredY);
            maxX = Math.Max(maxX, s.DesiredX); maxY = Math.Max(maxY, s.DesiredY);
        }
        if (count == 0) return true;
        int x0 = Math.Max(0, (int)Math.Floor(minX) - 1), y0 = Math.Max(0, (int)Math.Floor(minY) - 1);
        int x1 = Math.Min(world.Width - 1, (int)Math.Floor(maxX) + 1), y1 = Math.Min(world.Height - 1, (int)Math.Floor(maxY) + 1);
        Vec right = rigid.Body.GetLocalVector(new Vec(1, 0)), down = rigid.Body.GetLocalVector(new Vec(0, 1));
        // Rasterize the whole shape first. Rounding particle centres individually
        // leaves interior holes during rotation, even if particle count is preserved.
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            int cell = y * world.Width + x;
            if (!CanOccupy(cell)) continue;
            Vec local = rigid.Body.GetLocalPoint(new Vec((x + .5f) * Unit, (y + .5f) * Unit)) / Unit;
            int covered = 0;
            for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
            {
                Vec p = local + right * ((sx + .5f) / 4 - .5f) + down * ((sy + .5f) / 4 - .5f);
                if (rigid.Sites.Contains(((int)Math.Floor(p.X), (int)Math.Floor(p.Y)))) covered++;
            }
            double dx = local.X - Math.Floor(local.X) - .5, dy = local.Y - Math.Floor(local.Y) - .5;
            if (covered == 0)
            {
                bool nearby = false;
                for (int yy = -1; yy <= 1 && !nearby; yy++) for (int xx = -1; xx <= 1; xx++)
                    if (rigid.Sites.Contains(((int)Math.Floor(local.X) + xx, (int)Math.Floor(local.Y) + yy))) { nearby = true; break; }
                if (!nearby) continue;
            }
            pixelCandidates.Add(new(cell, covered, dx * dx + dy * dy));
        }
        if (pixelCandidates.Count < count) return false;
        pixelCandidates.Sort(static (a, b) =>
        {
            int order = b.Coverage.CompareTo(a.Coverage);
            if (order == 0) order = a.Error.CompareTo(b.Error);
            return order == 0 ? a.Cell.CompareTo(b.Cell) : order;
        });
        for (int i = 0; i < count; i++)
        {
            int cell = pixelCandidates[i].Cell;
            selectedPixels.Add(cell); rasterMarks[cell] = rasterGeneration;
        }
        // A local augmenting-path match moves each original material sample to
        // exactly one selected pixel, preserving mass, heat and material identity.
        foreach (var sample in rigid.Samples)
            if (sample.Target != -1 && !MatchPixel(sample, 3) && !MatchPixel(sample, 4)) return false;
        return true;
    }

    private bool MatchPixel(Sample root, int limit)
    {
        if (++matchGeneration == int.MaxValue)
        {
            foreach (var r in work) foreach (var s in r.Samples) s.MatchStamp = 0;
            matchGeneration = 1;
        }
        matchQueue.Clear(); matchQueue.Add(root); root.MatchStamp = matchGeneration; root.MatchParent = null;
        for (int n = 0; n < matchQueue.Count; n++)
        {
            var sample = matchQueue[n];
            int cx = (int)Math.Floor(sample.DesiredX), cy = (int)Math.Floor(sample.DesiredY);
            for (int radius = 0; radius <= limit; radius++)
            for (int y = cy - radius; y <= cy + radius; y++) for (int x = cx - radius; x <= cx + radius; x++)
            {
                if (!world.Inside(x, y) || radius > 0 && Math.Abs(x - cx) != radius && Math.Abs(y - cy) != radius) continue;
                int cell = y * world.Width + x;
                if (!selectedPixels.Contains(cell)) continue;
                if (!pixelOwners.TryGetValue(cell, out var owner))
                {
                    var current = sample; int free = cell;
                    while (true)
                    {
                        int old = current.Target;
                        current.Target = free; pixelOwners[free] = current;
                        if (current == root) return true;
                        free = old; current = current.MatchParent!;
                    }
                }
                if (owner.MatchStamp == matchGeneration) continue;
                owner.MatchStamp = matchGeneration; owner.MatchParent = sample; matchQueue.Add(owner);
            }
        }
        return false;
    }
}
