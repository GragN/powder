namespace Powder;

public enum Element : byte
{
    Empty, Sand, Water, Stone, Wood, Oil, Salt, Iron, Copper, Glass, Ice, Steam, Smoke, Fire, Lava
}

public enum MatterState { Empty, Powder, Liquid, Solid, Gas, Flame }

public readonly record struct Rgb(byte R, byte G, byte B);

// SI units: kg/m³, J/(kg·K), W/(m·K). Representative values near room temperature.
public sealed record Material(Element Id, string Name, MatterState State, Rgb Color,
    double Density, double SpecificHeat, double Conductivity, double SpawnTemperature,
    string Description);

public static class Materials
{
    public static readonly Material[] All =
    [
        new(Element.Empty, "Erase", MatterState.Empty, new(35, 43, 57), 0, 1, 0, 20, "Remove matter with the brush."),
        new(Element.Sand, "Sand", MatterState.Powder, new(230, 189, 111), 1600, 830, .27, 20, "Granular silica. Melt and cool to make glass."),
        new(Element.Water, "Water", MatterState.Liquid, new(71, 149, 230), 998, 4184, .60, 20, "Freezes at 0 C. Boils at 100 C."),
        new(Element.Stone, "Stone", MatterState.Solid, new(126, 139, 157), 2700, 790, 2.5, 20, "Rigid walls. Melts near 1200 C."),
        new(Element.Wood, "Wood", MatterState.Solid, new(165, 109, 68), 650, 1700, .12, 20, "Insulator. Ignites above 300 C."),
        new(Element.Oil, "Oil", MatterState.Liquid, new(174, 149, 68), 850, 2000, .15, 20, "Floats on water. Ignites above 280 C."),
        new(Element.Salt, "Salt", MatterState.Powder, new(224, 228, 220), 2160, 880, 6.5, 20, "Dense crystals. Dissolves in water."),
        new(Element.Iron, "Iron", MatterState.Solid, new(160, 171, 182), 7874, 449, 80, 20, "Heavy conductor. Melts at 1538 C."),
        new(Element.Copper, "Copper", MatterState.Solid, new(214, 137, 87), 8960, 385, 401, 20, "Excellent heat conductor. Melts at 1085 C."),
        new(Element.Glass, "Glass", MatterState.Solid, new(119, 191, 182), 2500, 840, 1.05, 20, "Transparent-looking, insulating solid."),
        new(Element.Ice, "Ice", MatterState.Solid, new(172, 225, 242), 917, 2100, 2.22, -20, "Absorbs latent heat before melting."),
        new(Element.Steam, "Steam", MatterState.Gas, new(181, 202, 219), .598, 1996, .025, 120, "Hot vapor. Condenses after releasing latent heat."),
        new(Element.Smoke, "Smoke", MatterState.Gas, new(98, 106, 121), 1.2, 1000, .03, 130, "Buoyant combustion products. Cools in air."),
        new(Element.Fire, "Fire", MatterState.Flame, new(255, 155, 57), .3, 1100, .08, 900, "Short-lived ignition source. Heats nearby matter."),
        new(Element.Lava, "Lava", MatterState.Liquid, new(247, 94, 48), 2600, 1200, 2.0, 1450, "Molten rock. Solidifies below 1200 C.")
    ];

    public static Material Get(Element e) => All[(int)e];
    public static bool IsWater(Element e) => e is Element.Water or Element.Ice or Element.Steam;
}
