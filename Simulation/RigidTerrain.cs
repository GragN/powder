using PhysicsBody = nkast.Aether.Physics2D.Dynamics.Body;
using Vec = nkast.Aether.Physics2D.Common.Vector2;

namespace Powder;

public sealed partial class RigidBodyField
{
    private const int Chunk = 16;
    private readonly int chunksX;
    private readonly Element[] terrain;
    private readonly PhysicsBody?[] terrainBodies;
    private readonly bool[] dirtyChunks;

    private void UpdateTerrain()
    {
        for (int i = 0; i < terrain.Length; i++)
        {
            ref readonly var p = ref world.Cells[i];
            var state = Materials.Get(p.Element).State;
            Element next = state == MatterState.Powder || state == MatterState.Solid && p.RigidId <= 0 ? p.Element : Element.Empty;
            if (next == terrain[i]) continue;
            terrain[i] = next; dirtyChunks[i / world.Width / Chunk * chunksX + i % world.Width / Chunk] = true;
        }
        Span<bool> used = stackalloc bool[Chunk * Chunk];
        for (int c = 0; c < terrainBodies.Length; c++) if (dirtyChunks[c])
        {
            dirtyChunks[c] = false;
            if (terrainBodies[c] is { } old) engine!.Remove(old);
            terrainBodies[c] = null;
            int x0 = c % chunksX * Chunk, y0 = c / chunksX * Chunk;
            int width = Math.Min(Chunk, world.Width - x0), height = Math.Min(Chunk, world.Height - y0);
            used.Clear();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Element e = terrain[(y0 + y) * world.Width + x0 + x];
                if (e == Element.Empty || used[y * Chunk + x]) continue;
                int w = 1, h = 1;
                while (x + w < width && !used[y * Chunk + x + w]
                    && terrain[(y0 + y) * world.Width + x0 + x + w] == e) w++;
                while (y + h < height)
                {
                    bool same = true;
                    for (int xx = 0; xx < w; xx++) if (used[(y + h) * Chunk + x + xx]
                        || terrain[(y0 + y + h) * world.Width + x0 + x + xx] != e) { same = false; break; }
                    if (!same) break; h++;
                }
                for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) used[(y + yy) * Chunk + x + xx] = true;
                var body = terrainBodies[c] ??= engine!.CreateBody(new Vec(x0 * Unit, y0 * Unit));
                var fixture = body.CreateRectangle(w * Unit, h * Unit, GeometryDensity, new Vec((x + w * .5f) * Unit, (y + h * .5f) * Unit));
                fixture.Friction = Materials.Get(e).State == MatterState.Powder ? .8f : Friction(e);
                fixture.Restitution = .03f;
            }
        }
    }
}
