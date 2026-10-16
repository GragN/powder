namespace Powder;

public static class Scenes
{
    public static void LiquidConvection(World w)
    {
        w.Clear();
        // A finite copper heater under water, not a prescribed-temperature source.
        int x = w.Width / 2 - 72, y = w.Height - 32;
        Rect(w, x - 3, y - 102, 3, 106, Element.Glass);
        Rect(w, x + 144, y - 102, 3, 106, Element.Glass);
        Rect(w, x - 3, y, 150, 4, Element.Glass);
        Rect(w, x, y - 96, 144, 96, Element.Water, 20);
        Rect(w, x + 58, y - 17, 28, 12, Element.Water, 65);
        Rect(w, x + 56, y - 5, 32, 5, Element.Copper, 95);
    }

    public static void Convection(World w)
    {
        w.Clear();
        Rect(w, 12, w.Height - 12, w.Width - 24, 4, Element.Stone);
        Rect(w, w.Width / 2 - 20, w.Height - 24, 40, 8, Element.Copper, 500);
        Rect(w, w.Width / 2 + 10, w.Height / 2, 90, 4, Element.Glass);
    }

    public static void Demo(World w)
    {
        w.Clear();
        int floor = w.Height - 18;
        Rect(w, 12, floor, w.Width - 24, 5, Element.Stone);
        // Left: sand funnel above a two-layer liquid tank.
        int left = 28, right = w.Width / 3 - 10;
        Line(w, left, 97, 89, 141, Element.Glass, 3);
        Line(w, right, 97, 103, 141, Element.Glass, 3);
        for (int y = 88; y < 130; y++)
            for (int x = left + 8; x < right - 8; x++)
                if (x > left + (y - 88) && x < right - (y - 88)) w.Set(x, y, Element.Sand);
        Rect(w, 23, 201, 4, floor - 201, Element.Glass);
        Rect(w, right + 2, 201, 4, floor - 201, Element.Glass);
        Rect(w, 27, floor - 62, right - 25, 62, Element.Water);
        Rect(w, 27, floor - 75, right - 25, 13, Element.Oil);
        Rect(w, 54, floor - 92, 25, 17, Element.Ice);
        // Middle: a copper heat bridge linking a hot reservoir to cold water.
        int middle = w.Width / 3 + 14;
        Rect(w, middle, floor - 94, 4, 94, Element.Stone);
        Rect(w, middle + 145, floor - 94, 4, 94, Element.Stone);
        Rect(w, middle + 4, floor - 49, 141, 49, Element.Water, 35);
        Rect(w, middle + 4, floor - 57, 141, 7, Element.Copper, 180);
        Rect(w, middle + 44, floor - 122, 9, 65, Element.Copper, 600);
        Rect(w, middle + 30, floor - 148, 38, 25, Element.Lava, 1600);
        Rect(w, middle + 27, floor - 152, 3, 31, Element.Iron);
        Rect(w, middle + 68, floor - 152, 3, 31, Element.Iron);
        Rect(w, middle + 27, floor - 123, 44, 3, Element.Iron, 700);
        // Right: a combustible stack with a small ignition source.
        int fireX = w.Width * 2 / 3 + 35;
        for (int n = 0; n < 4; n++)
            Rect(w, fireX + (n % 2) * 6, floor - 9 - n * 10, 72, 8, Element.Wood);
        Rect(w, fireX + 24, floor - 38, 22, 5, Element.Wood, 450);
        Rect(w, fireX - 12, floor - 5, 98, 5, Element.Iron);
        // Small material samples on ledges.
        Rect(w, middle + 1, 112, 53, 3, Element.Stone);
        Rect(w, middle + 7, 98, 16, 14, Element.Salt);
        Rect(w, middle + 30, 99, 16, 13, Element.Sand);
        Rect(w, fireX - 5, 112, 95, 3, Element.Stone);
        Rect(w, fireX, 88, 22, 24, Element.Copper);
        Rect(w, fireX + 31, 88, 22, 24, Element.Iron);
        Rect(w, fireX + 62, 88, 22, 24, Element.Glass);
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
