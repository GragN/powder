namespace Powder;

public static class Scenes
{
    public static void Boiling(World w)
    {
        w.Clear();
        var scene = new SceneLayout(w);
        int x = SceneLayout.Width / 2 - 60, y = SceneLayout.Height - 32;
        scene.Rect(x - 3, y - 90, 3, 94, Element.Glass, 100);
        scene.Rect(x + 120, y - 90, 3, 94, Element.Glass, 100);
        scene.Rect(x - 3, y, 126, 4, Element.Glass, 100);
        scene.Rect(x, y - 80, 120, 80, Element.Water, 100);
        scene.Rect(x + 44, y - 5, 32, 5, Element.Copper, 130);
    }

    public static void LiquidConvection(World w)
    {
        w.Clear();
        var scene = new SceneLayout(w);
        // A finite copper heater under water, not a prescribed-temperature source.
        int x = SceneLayout.Width / 2 - 72, y = SceneLayout.Height - 32;
        scene.Rect(x - 3, y - 102, 3, 106, Element.Glass);
        scene.Rect(x + 144, y - 102, 3, 106, Element.Glass);
        scene.Rect(x - 3, y, 150, 4, Element.Glass);
        scene.Rect(x, y - 96, 144, 96, Element.Water, 20);
        scene.Rect(x + 58, y - 17, 28, 12, Element.Water, 65);
        scene.Rect(x + 56, y - 5, 32, 5, Element.Copper, 95);
    }

    public static void Convection(World w)
    {
        w.Clear();
        var scene = new SceneLayout(w);
        scene.Rect(12, SceneLayout.Height - 12, SceneLayout.Width - 24, 4, Element.Stone);
        scene.Rect(SceneLayout.Width / 2 - 20, SceneLayout.Height - 24, 40, 8, Element.Copper, 500);
        scene.Rect(SceneLayout.Width / 2 + 10, SceneLayout.Height / 2, 90, 4, Element.Glass);
    }

    public static void Demo(World w)
    {
        w.Clear();
        var scene = new SceneLayout(w);
        int floor = SceneLayout.Height - 18;
        scene.Rect(12, floor, SceneLayout.Width - 24, 5, Element.Stone);
        // Left: sand funnel above a two-layer liquid tank.
        int left = 28, right = SceneLayout.Width / 3 - 10;
        scene.Line(left, 97, 89, 141, Element.Glass, 3);
        scene.Line(right, 97, 103, 141, Element.Glass, 3);
        for (int y = 88; y < 130; y++)
            for (int x = left + 8; x < right - 8; x++)
                if (x > left + (y - 88) && x < right - (y - 88)) scene.Rect(x, y, 1, 1, Element.Sand);
        scene.Rect(23, 201, 4, floor - 201, Element.Glass);
        scene.Rect(right + 2, 201, 4, floor - 201, Element.Glass);
        scene.Rect(27, floor - 62, right - 25, 62, Element.Water);
        scene.Rect(27, floor - 75, right - 25, 13, Element.Oil);
        scene.Rect(54, floor - 92, 25, 17, Element.Ice);
        // Middle: a copper heat bridge linking a hot reservoir to cold water.
        int middle = SceneLayout.Width / 3 + 14;
        scene.Rect(middle, floor - 94, 4, 94, Element.Stone);
        scene.Rect(middle + 145, floor - 94, 4, 94, Element.Stone);
        scene.Rect(middle + 4, floor - 49, 141, 49, Element.Water, 35);
        scene.Rect(middle + 4, floor - 57, 141, 7, Element.Copper, 180);
        scene.Rect(middle + 44, floor - 122, 9, 65, Element.Copper, 600);
        scene.Rect(middle + 30, floor - 148, 38, 25, Element.Lava, 1600);
        scene.Rect(middle + 27, floor - 152, 3, 31, Element.Iron);
        scene.Rect(middle + 68, floor - 152, 3, 31, Element.Iron);
        scene.Rect(middle + 27, floor - 123, 44, 3, Element.Iron, 700);
        // Right: a combustible stack with a small ignition source.
        int fireX = SceneLayout.Width * 2 / 3 + 35;
        for (int n = 0; n < 4; n++)
            scene.Rect(fireX + (n % 2) * 6, floor - 9 - n * 10, 72, 8, Element.Wood);
        scene.Rect(fireX + 24, floor - 38, 22, 5, Element.Wood, 450);
        scene.Rect(fireX - 12, floor - 5, 98, 5, Element.Iron);
        // Small material samples on ledges.
        scene.Rect(middle + 1, 112, 53, 3, Element.Stone);
        scene.Rect(middle + 7, 98, 16, 14, Element.Salt);
        scene.Rect(middle + 30, 99, 16, 13, Element.Sand);
        scene.Rect(fireX - 5, 112, 95, 3, Element.Stone);
        scene.Rect(fireX, 88, 22, 24, Element.Copper);
        scene.Rect(fireX + 31, 88, 22, 24, Element.Iron);
        scene.Rect(fireX + 62, 88, 22, 24, Element.Glass);
    }

    public static void Rect(World w, int x, int y, int width, int height, Element e, double? t = null)
    {
        for (int yy = y; yy < y + height; yy++)
            for (int xx = x; xx < x + width; xx++) w.Set(xx, yy, e, t);
    }

    public static void Line(World w, int x0, int y0, int x1, int y1, Element e, int radius)
    {
        int count = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (int i = 0; i <= count; i++)
        {
            float t = count == 0 ? 0 : (float)i / count;
            w.Brush((int)MathF.Round(x0 + (x1 - x0) * t), (int)MathF.Round(y0 + (y1 - y0) * t), radius, e, true);
        }
    }
}

// Built-in scenes use a common reference canvas. Scale geometry, not particle
// size or material properties, so smaller worlds retain the same arrangement.
internal readonly struct SceneLayout(World world)
{
    public const int Width = 480, Height = 336;
    public int X(int x) => (int)Math.Round(x * world.Width / (double)Width, MidpointRounding.AwayFromZero);
    public int Y(int y) => (int)Math.Round(y * world.Height / (double)Height, MidpointRounding.AwayFromZero);
    public int Radius(int radius) => Math.Max(1, (int)Math.Round(radius * Math.Min(world.Width / (double)Width,
        world.Height / (double)Height), MidpointRounding.AwayFromZero));

    public void Rect(int x, int y, int width, int height, Element e, double? temperature = null)
        => Scenes.Rect(world, X(x), Y(y), Math.Max(1, X(x + width) - X(x)),
            Math.Max(1, Y(y + height) - Y(y)), e, temperature);

    public void Line(int x0, int y0, int x1, int y1, Element e, int radius)
        => Scenes.Line(world, X(x0), Y(y0), X(x1), Y(y1), e, Radius(radius));
}
