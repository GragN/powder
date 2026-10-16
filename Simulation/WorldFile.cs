using System.Text.Json;

namespace Powder;

public static class WorldFile
{
    private sealed record Cell(int Index, Element Element, Element Origin, double Mass,
        double Energy, double Salt, double Velocity, double Travel, float Life, byte Variation,
        double EnthalpyOffset = 0, double VelocityX = 0, double TravelX = 0);
    private sealed record Snapshot(int Version, int Width, int Height, bool Cooling, Cell[] Cells,
        AirField.State? Air = null, LiquidField.State? Liquids = null);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowderLab", "scene.json");

    public static void Save(World world, string path)
    {
        var cells = world.Cells.Select((p, i) => new Cell(i, p.Element, p.MeltOrigin, p.Mass,
            p.Energy, p.DissolvedSalt, p.VelocityY, p.Travel, p.Life, p.Variation, p.EnthalpyOffset, p.VelocityX, p.TravelX))
            .Where(p => p.Element != Element.Empty).ToArray();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Snapshot(3, world.Width, world.Height,
            world.AtmosphereEnabled, cells, world.Air.Capture(), world.Liquids.Capture()), Options));
        File.Move(temp, full, true);
    }

    public static World Load(string path, int expectedWidth, int expectedHeight)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Scene is too large.");
        var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Empty scene.");
        if (s.Version is not (1 or 2 or 3) || s.Width != expectedWidth || s.Height != expectedHeight || s.Cells is null)
            throw new InvalidDataException("Unsupported scene version or dimensions.");
        var world = new World(s.Width, s.Height) { AtmosphereEnabled = s.Cooling };
        var occupied = new HashSet<int>();
        foreach (var c in s.Cells)
        {
            if (c is null || c.Index < 0 || c.Index >= world.Cells.Length || !occupied.Add(c.Index)
                || !Enum.IsDefined(c.Element) || c.Element is Element.Empty or Element.Air || !Enum.IsDefined(c.Origin)
                || c.Element == Element.Lava && !Thermodynamics.CanMelt(c.Origin)
                || !double.IsFinite(c.Mass) || c.Mass <= 0 || !double.IsFinite(c.Energy)
                || !double.IsFinite(c.Salt) || c.Salt < 0 || c.Salt > c.Mass
                || !double.IsFinite(c.Velocity) || !double.IsFinite(c.Travel)
                || c.Travel <= -1 || c.Travel >= 1 || !float.IsFinite(c.Life) || !double.IsFinite(c.EnthalpyOffset)
                || !double.IsFinite(c.VelocityX) || !double.IsFinite(c.TravelX) || c.TravelX <= -1 || c.TravelX >= 1)
                throw new InvalidDataException("Invalid particle data.");
            world.Cells[c.Index] = new Particle
            {
                Element = c.Element, MeltOrigin = c.Origin, Mass = c.Mass, Energy = c.Energy,
                DissolvedSalt = c.Salt, VelocityY = c.Velocity, Travel = c.Travel,
                Life = c.Life, Variation = c.Variation, EnthalpyOffset = c.EnthalpyOffset,
                VelocityX = c.VelocityX, TravelX = c.TravelX
            };
            if (s.Version < 3 && c.Element == Element.Lava)
            {
                // Older files used 1200 J/(kg K) for every melt. Preserve their temperature.
                ref var p = ref world.Cells[c.Index];
                double end = p.Mass * (p.EnthalpyOffset + Materials.Get(p.MeltOrigin).SpecificHeat
                    * Thermodynamics.MeltingPoint(p.MeltOrigin) + Thermodynamics.FusionHeat(p.MeltOrigin));
                if (p.Energy > end) p.Energy = end + (p.Energy - end) * LiquidProperties.MeltHeatCapacity(p.MeltOrigin) / 1200;
            }
        }
        world.Air.UpdateObstacles(world);
        if (s.Air is not null) world.Air.Restore(s.Air);
        else if (s.Version >= 2) throw new InvalidDataException("Missing atmosphere data.");
        if (s.Liquids is not null) world.Liquids.Restore(world, s.Liquids);
        else if (s.Version >= 3) throw new InvalidDataException("Missing liquid flow data.");
        return world;
    }
}
