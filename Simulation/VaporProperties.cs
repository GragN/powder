namespace Powder;

// Approximate normal boiling data at 1 atm. Oil, rock and silica are simplified
// single substances; real mixtures boil over a range and may decompose.
public static class VaporProperties
{
    public static bool Supports(Element origin) => origin == Element.Oil || Thermodynamics.CanMelt(origin);
    public static double BoilingPoint(Element origin) => origin switch
    {
        Element.Water => 100, Element.Oil => 350, Element.Copper => 2562,
        Element.Iron => 2862, Element.Sand or Element.Glass => 2950, Element.Stone => 2800,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    public static double LatentHeat(Element origin) => origin switch
    {
        Element.Water => Thermodynamics.Vaporization, Element.Oil => 250_000,
        Element.Copper => 4_730_000, Element.Iron => 6_090_000,
        Element.Sand or Element.Glass => 9_000_000, Element.Stone => 5_000_000,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    public static double SpecificHeat(Element origin) => origin switch
    { Element.Water => 1996, Element.Oil => 1700, Element.Copper => 327, Element.Iron => 372, _ => 1000 };
    public static double Conductivity(Element origin) => origin == Element.Oil ? .025 : .04;
    public static double Density(Element origin, double temperature)
    {
        double molarMass = origin switch
        { Element.Oil => .2, Element.Copper => .063546, Element.Iron => .055845, _ => .06008 };
        return 101325 * molarMass / (8.314462618 * Math.Max(50, temperature + 273.15));
    }
    public static string Name(Element origin) => origin switch
    {
        Element.Stone => "Rock vapor", Element.Sand or Element.Glass => "Silica vapor",
        _ => Materials.Get(origin).Name + " vapor"
    };
    public static double LiquidEnthalpy(Element origin, double temperature) => origin == Element.Oil
        ? Materials.Get(Element.Oil).SpecificHeat * temperature : Thermodynamics.LiquidEnthalpy(origin, temperature);
    public static double BoilingLiquid(Element origin) => LiquidEnthalpy(origin, BoilingPoint(origin));
    public static double BoilingGas(Element origin) => BoilingLiquid(origin) + LatentHeat(origin);
}
