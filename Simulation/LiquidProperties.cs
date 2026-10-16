namespace Powder;

// SI units. Oil is represented by a medium mineral oil; rock by a basaltic melt.
// These are representative constitutive laws, rather than coefficients shared by all liquids.
public static class LiquidProperties
{
    public static int Kind(in Particle p) => p.Element switch
    {
        Element.Water => 1, Element.Oil => 2,
        Element.Lava => 16 + (int)p.MeltOrigin, _ => 0
    };

    public static double MeltHeatCapacity(Element origin) => origin switch
    { Element.Copper => 520, Element.Iron => 820, _ => 1200 };

    public static double Conductivity(in Particle p) => p.Element == Element.Lava ? p.MeltOrigin switch
    { Element.Copper => 160, Element.Iron => 35, Element.Sand or Element.Glass => 1.5, _ => 2 }
        : Materials.Get(p.Element).Conductivity;

    public static double ReferenceDensity(in Particle p) => p.Element switch
    {
        Element.Water => WaterDensity(20), Element.Oil => 850,
        Element.Lava => p.MeltOrigin switch
        { Element.Copper => 8020, Element.Iron => 7000, Element.Sand or Element.Glass => 2200, _ => 2600 },
        _ => Materials.Get(p.Element).Density
    };

    // Pure water has its density maximum near 4 C, not at the freezing point.
    private static double WaterDensity(double t)
    {
        t = Math.Clamp(t, 0, 100);
        return 1000 * (1 - (t + 288.9414) / (508929.2 * (t + 68.12963)) * (t - 3.9863) * (t - 3.9863));
    }

    public static double Density(in Particle p)
    {
        double t = p.Temperature, reference = ReferenceDensity(p);
        return p.Element switch
        {
            Element.Water => WaterDensity(t) + 750 * p.DissolvedSalt / p.Mass,
            Element.Oil => reference / Math.Max(.5, 1 + .0007 * (t - 20)),
            Element.Lava => reference / Math.Max(.5, 1 + (p.MeltOrigin is Element.Glass or Element.Sand ? .000015 : .0001)
                * (t - (p.MeltOrigin == Element.Stone ? 1450 : Thermodynamics.MeltingPoint(p.MeltOrigin)))),
            _ => reference
        };
    }

    public static double DynamicViscosity(in Particle p)
    {
        double t = p.Temperature;
        double mu = p.Element switch
        {
            // Andrade relation for water: about 1.00 mPa s at 20 C, 0.28 at 100 C.
            Element.Water => 2.414e-5 * Math.Pow(10, 247.8 / (Math.Clamp(t, 0, 100) + 133.15)),
            Element.Oil => .05 * Math.Exp(-.025 * (t - 20)),
            Element.Lava => p.MeltOrigin switch
            {
                Element.Copper => .004 * Math.Exp(-.002 * (t - 1085)),
                Element.Iron => .006 * Math.Exp(-.002 * (t - 1538)),
                Element.Sand or Element.Glass => Math.Pow(10, 6 - .008 * (t - 1700)),
                _ => 500 * Math.Exp(-.012 * (t - 1200))
            },
            _ => 0
        };
        return Math.Clamp(mu, 1e-5, 1e9);
    }

    public static double KinematicViscosity(in Particle p) => DynamicViscosity(p) / Density(p);
}
