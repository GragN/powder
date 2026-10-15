namespace Powder;

public struct Particle
{
    public Element Element;
    public Element MeltOrigin;
    public double Mass;          // kg, retained during movement and phase changes
    public double Energy;        // J, includes phase-transition energy
    public double DissolvedSalt; // kg, included in Mass
    public float VelocityY;      // cells / second
    public float Travel;         // fractional cells
    public float Life;
    public uint Stamp;
    public byte Variation;

    public readonly double HeatCapacity => Math.Max(1e-12,
        (Mass - DissolvedSalt) * Materials.Get(Element).SpecificHeat + DissolvedSalt * 880);

    public readonly double Temperature => Thermodynamics.Temperature(this);
}

public static class Thermodynamics
{
    public const double Fusion = 334_000;
    public const double Vaporization = 2_256_000;
    public const double BoilingLiquid = Fusion + 4184 * 100;
    public const double BoilingGas = BoilingLiquid + Vaporization;

    public static bool CanMelt(Element e) => e is Element.Stone or Element.Iron or Element.Copper or Element.Glass or Element.Sand;
    public static double MeltingPoint(Element e) => e switch
    {
        Element.Iron => 1538, Element.Copper => 1085,
        Element.Sand or Element.Glass => 1700, _ => 1200
    };
    public static double FusionHeat(Element e) => e switch
    {
        Element.Iron => 247_000, Element.Copper => 205_000, _ => 400_000
    };

    public static double LiquidEnthalpy(Element origin, double t) =>
        Materials.Get(origin).SpecificHeat * MeltingPoint(origin) + FusionHeat(origin)
        + Materials.Get(Element.Lava).SpecificHeat * (t - MeltingPoint(origin));

    public static double SpecificEnthalpy(Element e, double t) => e switch
    {
        Element.Ice => 2100 * Math.Min(t, 0),
        Element.Water => Fusion + 4184 * Math.Clamp(t, 0, 100),
        Element.Steam => BoilingGas + 1996 * (Math.Max(t, 100) - 100),
        Element.Lava => LiquidEnthalpy(Element.Stone, t),
        _ => Materials.Get(e).SpecificHeat * t
    };

    public static double Temperature(in Particle p)
    {
        if (p.Element == Element.Empty) return 20;
        if (p.Element == Element.Lava)
        {
            double originCapacity = Materials.Get(p.MeltOrigin).SpecificHeat;
            double melt = MeltingPoint(p.MeltOrigin);
            double h = p.Energy / p.Mass;
            double end = originCapacity * melt + FusionHeat(p.MeltOrigin);
            return h >= end ? melt + (h - end) / Materials.Get(Element.Lava).SpecificHeat
                : h >= originCapacity * melt ? melt : h / originCapacity;
        }
        if (CanMelt(p.Element))
        {
            double melt = MeltingPoint(p.Element), cp = Materials.Get(p.Element).SpecificHeat;
            double h = p.Energy / p.Mass;
            if (h >= cp * melt) return h < cp * melt + FusionHeat(p.Element) ? melt
                : melt + (h - cp * melt - FusionHeat(p.Element)) / Materials.Get(Element.Lava).SpecificHeat;
        }
        if (!Materials.IsWater(p.Element)) return p.Energy / p.HeatCapacity;
        double waterMass = Math.Max(1e-12, p.Mass - p.DissolvedSalt);
        // Salt's sensible heat is part of the same energy budget.
        if (p.Energy < 0) return p.Energy / (waterMass * 2100 + p.DissolvedSalt * 880);
        if (p.Energy < waterMass * Fusion) return 0;
        double capacity = waterMass * 4184 + p.DissolvedSalt * 880;
        double boilStart = waterMass * Fusion + capacity * 100;
        if (p.Energy < boilStart) return (p.Energy - waterMass * Fusion) / capacity;
        double boilEnd = boilStart + waterMass * Vaporization;
        if (p.Energy < boilEnd) return 100;
        return 100 + (p.Energy - boilEnd) / (waterMass * 1996 + p.DissolvedSalt * 880);
    }

    public static void ResolvePhase(ref Particle p)
    {
        if (CanMelt(p.Element))
        {
            double end = Materials.Get(p.Element).SpecificHeat * MeltingPoint(p.Element) + FusionHeat(p.Element);
            if (p.Energy / p.Mass >= end)
            { p.MeltOrigin = p.Element; p.Element = Element.Lava; }
            return;
        }
        if (p.Element == Element.Lava)
        {
            double solid = Materials.Get(p.MeltOrigin).SpecificHeat * MeltingPoint(p.MeltOrigin);
            if (p.Energy / p.Mass <= solid) p.Element = p.MeltOrigin;
            return;
        }
        if (!Materials.IsWater(p.Element)) return;
        double waterMass = Math.Max(1e-12, p.Mass - p.DissolvedSalt);
        double boilEnd = waterMass * BoilingGas + p.DissolvedSalt * 880 * 100;
        // A whole cell changes state only after paying the latent heat cost.
        p.Element = p.Energy < waterMass * Fusion ? Element.Ice
            : p.Energy < boilEnd ? Element.Water : Element.Steam;
    }

    public static void Exchange(ref Particle a, ref Particle b, double seconds)
    {
        if (a.Element == Element.Empty || b.Element == Element.Empty) return;
        double ka = Materials.Get(a.Element).Conductivity;
        double kb = Materials.Get(b.Element).Conductivity;
        double conductance = 2 * ka * kb / (ka + kb) * World.Depth;
        double inverseCapacity = 1 / a.HeatCapacity + 1 / b.HeatCapacity;
        double q = (b.Temperature - a.Temperature) / inverseCapacity
            * (1 - Math.Exp(-conductance * inverseCapacity * seconds));
        a.Energy += q;
        b.Energy -= q;
    }
}
