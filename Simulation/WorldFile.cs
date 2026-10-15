using System.Text.Json;

namespace Powder;

public static class WorldFile
{
    private sealed record Cell(int Index, Element Element, Element Origin, double Mass,
        double Energy, double Salt, float Velocity, float Travel, float Life, byte Variation);
    private sealed record Snapshot(int Version, int Width, int Height, bool Cooling, Cell[] Cells);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowderLab", "scene.json");

    public static void Save(World world, string path)
    {
        var cells = world.Cells.Select((p, i) => new Cell(i, p.Element, p.MeltOrigin, p.Mass,
            p.Energy, p.DissolvedSalt, p.VelocityY, p.Travel, p.Life, p.Variation))
            .Where(p => p.Element != Element.Empty).ToArray();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Snapshot(1, world.Width, world.Height,
            world.AmbientCooling, cells), Options));
        File.Move(temp, full, true);
    }

    public static World Load(string path, int expectedWidth, int expectedHeight)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Scene is too large.");
        var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Empty scene.");
        if (s.Version != 1 || s.Width != expectedWidth || s.Height != expectedHeight || s.Cells is null)
            throw new InvalidDataException("Unsupported scene version or dimensions.");
        var world = new World(s.Width, s.Height) { AmbientCooling = s.Cooling };
        var occupied = new HashSet<int>();
        foreach (var c in s.Cells)
        {
            if (c is null || c.Index < 0 || c.Index >= world.Cells.Length || !occupied.Add(c.Index)
                || !Enum.IsDefined(c.Element) || c.Element == Element.Empty || !Enum.IsDefined(c.Origin)
                || c.Element == Element.Lava && !Thermodynamics.CanMelt(c.Origin)
                || !double.IsFinite(c.Mass) || c.Mass <= 0 || !double.IsFinite(c.Energy)
                || !double.IsFinite(c.Salt) || c.Salt < 0 || c.Salt > c.Mass
                || !float.IsFinite(c.Velocity) || !float.IsFinite(c.Travel) || !float.IsFinite(c.Life))
                throw new InvalidDataException("Invalid particle data.");
            world.Cells[c.Index] = new Particle
            {
                Element = c.Element, MeltOrigin = c.Origin, Mass = c.Mass, Energy = c.Energy,
                DissolvedSalt = c.Salt, VelocityY = c.Velocity, Travel = c.Travel,
                Life = c.Life, Variation = c.Variation
            };
        }
        return world;
    }
}
