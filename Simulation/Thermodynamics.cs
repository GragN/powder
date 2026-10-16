namespace Powder;

// Internal energy is stored in joules. Temperature is recovered from a continuous,
// piecewise-linear enthalpy curve, including constant-temperature latent intervals.
public static class Thermodynamics
{
    public const double Fusion = 334_000;
    public const double Vaporization = 2_256_000;
    public static double BoilingLiquid => Fusion + WaterHeat * 100;
    public static double BoilingGas => BoilingLiquid + Vaporization;
    private static double IceHeat => Materials.Get(Element.Ice).SpecificHeat;
    private static double WaterHeat => Materials.Get(Element.Water).SpecificHeat;
    private static double SteamHeat => Materials.Get(Element.Steam).SpecificHeat;
    private static double SaltHeat => Materials.Get(Element.Salt).SpecificHeat;

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
        + LiquidProperties.MeltHeatCapacity(origin) * (t - MeltingPoint(origin));

    public static double SpecificEnthalpy(Element e, double t)
    {
        if (Materials.IsWater(e))
        {
            if (t < 0 || e == Element.Ice && t == 0) return IceHeat * t;
            if (t > 100 || e == Element.Steam && t == 100) return BoilingGas + SteamHeat * (t - 100);
            return Fusion + WaterHeat * t;
        }
        Element origin = e is Element.Lava or Element.Vapor ? Element.Stone : e;
        if (VaporProperties.Supports(origin) && (t > VaporProperties.BoilingPoint(origin)
            || e == Element.Vapor && t == VaporProperties.BoilingPoint(origin)))
            return VaporProperties.BoilingGas(origin) + VaporProperties.SpecificHeat(origin) * (t - VaporProperties.BoilingPoint(origin));
        if ((e is Element.Lava or Element.Vapor) && t >= MeltingPoint(origin) || CanMelt(e) && t > MeltingPoint(e))
            return LiquidEnthalpy(origin, t);
        return Materials.Get(origin).SpecificHeat * t;
    }

    public static double EnergyAtTemperature(in Particle p, double t)
    {
        if (Materials.IsWater(p.Element))
            return (p.Mass - p.DissolvedSalt) * SpecificEnthalpy(Element.Water, t)
                + p.DissolvedSalt * SaltHeat * t;
        Element origin = p.Element is Element.Lava or Element.Vapor ? p.MeltOrigin : p.Element;
        return p.Mass * (p.EnthalpyOffset + SpecificEnthalpy(origin, t));
    }

    public static double Temperature(in Particle p)
    {
        if (p.Element == Element.Empty) return 20;
        if (Materials.IsWater(p.Element))
        {
            double waterMass = p.Mass - p.DissolvedSalt;
            double iceCapacity = waterMass * IceHeat + p.DissolvedSalt * SaltHeat;
            double liquidCapacity = waterMass * WaterHeat + p.DissolvedSalt * SaltHeat;
            double gasCapacity = waterMass * SteamHeat + p.DissolvedSalt * SaltHeat;
            double fusionEnd = waterMass * Fusion;
            double boilStart = fusionEnd + liquidCapacity * 100;
            double boilEnd = boilStart + waterMass * Vaporization;
            if (p.Energy < 0) return p.Energy / iceCapacity;
            if (p.Energy < fusionEnd) return 0;
            if (p.Energy < boilStart) return (p.Energy - fusionEnd) / liquidCapacity;
            if (p.Energy < boilEnd) return 100;
            return 100 + (p.Energy - boilEnd) / gasCapacity;
        }
        Element origin = p.Element is Element.Lava or Element.Vapor ? p.MeltOrigin : p.Element;
        double h = p.Energy / p.Mass - p.EnthalpyOffset;
        if (VaporProperties.Supports(origin))
        {
            double start = VaporProperties.BoilingLiquid(origin), end = VaporProperties.BoilingGas(origin);
            if (h >= end) return VaporProperties.BoilingPoint(origin) + (h - end) / VaporProperties.SpecificHeat(origin);
            if (h >= start) return VaporProperties.BoilingPoint(origin);
        }
        double cp = Materials.Get(origin).SpecificHeat;
        if (CanMelt(origin))
        {
            double melt = MeltingPoint(origin), start = cp * melt;
            double end = start + FusionHeat(origin);
            if (h >= end) return melt + (h - end) / LiquidProperties.MeltHeatCapacity(origin);
            if (h >= start) return melt;
        }
        return h / cp;
    }

    public static void ResolvePhase(ref Particle p)
    {
        Element before = p.Element;
        Element origin = p.Element is Element.Lava or Element.Vapor ? p.MeltOrigin : p.Element;
        if (VaporProperties.Supports(origin))
        {
            double boilStart = p.Mass * (p.EnthalpyOffset + VaporProperties.BoilingLiquid(origin));
            double boilEnd = p.Mass * (p.EnthalpyOffset + VaporProperties.BoilingGas(origin));
            p.MeltOrigin = origin;
            if (p.Energy >= boilEnd || before == Element.Vapor && p.Energy > boilStart)
                p.Element = Element.Vapor;
            else if (origin == Element.Oil) p.Element = Element.Oil;
            else
            {
                double cp = Materials.Get(origin).SpecificHeat;
                double meltStart = p.Mass * (p.EnthalpyOffset + cp * MeltingPoint(origin));
                double meltEnd = meltStart + p.Mass * FusionHeat(origin);
                if (p.Energy >= meltEnd || (before is Element.Lava or Element.Vapor) && p.Energy > meltStart)
                    p.Element = Element.Lava;
                else
                {
                    Element solid = origin == Element.Sand && (before is Element.Lava or Element.Vapor) ? Element.Glass : origin;
                    // Silica's enthalpy reference must survive evaporation as well as melting.
                    p.EnthalpyOffset += (cp - Materials.Get(solid).SpecificHeat) * MeltingPoint(origin);
                    p.Element = p.MeltOrigin = solid;
                }
            }
        }
        else if (Materials.IsWater(p.Element))
        {
            double waterMass = p.Mass - p.DissolvedSalt;
            double fusionEnd = waterMass * Fusion;
            double boilStart = waterMass * BoilingLiquid + p.DissolvedSalt * SaltHeat * 100;
            double boilEnd = boilStart + waterMass * Vaporization;
            // Keep the original whole-cell state inside a mixed-phase interval.
            // Cooling steam/water must release all latent heat before changing state.
            p.Element = p.Energy <= 0 ? Element.Ice
                : p.Energy < fusionEnd ? before == Element.Ice ? Element.Ice : Element.Water
                : p.Energy >= boilEnd ? Element.Steam
                : p.Energy > boilStart && before == Element.Steam ? Element.Steam : Element.Water;
        }
        if (p.Element != before && Materials.Get(p.Element).State == MatterState.Solid)
        { p.VelocityX = p.VelocityY = 0; p.TravelX = p.Travel = 0; }
    }

    // The heat capacity and next energy boundary in the direction of heat flow.
    // Infinite capacity represents a latent interval, not a numerical approximation.
    private static (double Capacity, double Boundary) Segment(in Particle p, bool heating)
    {
        if (Materials.IsWater(p.Element))
        {
            double m = p.Mass - p.DissolvedSalt, salt = p.DissolvedSalt * SaltHeat;
            double a = m * Fusion, b = a + (m * WaterHeat + salt) * 100, c = b + m * Vaporization;
            return SelectSegment(p.Energy, heating, 0, a, b, c,
                m * IceHeat + salt, m * WaterHeat + salt, m * SteamHeat + salt);
        }
        Element origin = p.Element is Element.Lava or Element.Vapor ? p.MeltOrigin : p.Element;
        if (VaporProperties.Supports(origin))
        {
            double c = p.Mass * (p.EnthalpyOffset + VaporProperties.BoilingLiquid(origin));
            double d = c + p.Mass * VaporProperties.LatentHeat(origin);
            if (CanMelt(origin))
            {
                double cp = Materials.Get(origin).SpecificHeat;
                double a = p.Mass * (p.EnthalpyOffset + cp * MeltingPoint(origin));
                double b = a + p.Mass * FusionHeat(origin);
                return SelectSegment(p.Energy, heating, a, b, c, d,
                    p.Mass * cp, p.Mass * LiquidProperties.MeltHeatCapacity(origin), p.Mass * VaporProperties.SpecificHeat(origin));
            }
            double epsilon = 1e-12 * Math.Max(1, Math.Abs(p.Energy));
            if (heating)
            {
                if (p.Energy < c - epsilon) return (p.Mass * Materials.Get(origin).SpecificHeat, c);
                if (p.Energy < d - epsilon) return (double.PositiveInfinity, d);
                return (p.Mass * VaporProperties.SpecificHeat(origin), double.PositiveInfinity);
            }
            if (p.Energy > d + epsilon) return (p.Mass * VaporProperties.SpecificHeat(origin), d);
            if (p.Energy > c + epsilon) return (double.PositiveInfinity, c);
            return (p.Mass * Materials.Get(origin).SpecificHeat, double.NegativeInfinity);
        }
        return (p.HeatCapacity, heating ? double.PositiveInfinity : double.NegativeInfinity);
    }

    private static (double Capacity, double Boundary) SelectSegment(double energy, bool heating,
        double a, double b, double c, double d, double ice, double water, double steam)
    {
        double epsilon = 1e-12 * Math.Max(1, Math.Abs(energy));
        if (heating)
        {
            if (energy < a - epsilon) return (ice, a);
            if (energy < b - epsilon) return (double.PositiveInfinity, b);
            if (energy < c - epsilon) return (water, c);
            if (energy < d - epsilon) return (double.PositiveInfinity, d);
            return (steam, double.PositiveInfinity);
        }
        if (energy > d + epsilon) return (steam, d);
        if (energy > c + epsilon) return (double.PositiveInfinity, c);
        if (energy > b + epsilon) return (water, b);
        if (energy > a + epsilon) return (double.PositiveInfinity, a);
        return (ice, double.NegativeInfinity);
    }

    private static double Transfer(double difference, double inverseCapacity, double conductance, double time)
    {
        if (inverseCapacity == 0) return conductance * difference * time;
        double x = conductance * inverseCapacity * time;
        double factor = x < 1e-5 ? x * (1 - x / 2 + x * x / 6) : 1 - Math.Exp(-x);
        return difference / inverseCapacity * factor;
    }

    private static double TimeToBoundary(double amount, double difference, double inverseCapacity, double conductance)
    {
        if (inverseCapacity == 0) return amount / (conductance * difference);
        double ratio = Math.Clamp(amount * inverseCapacity / difference, 0, 1 - 1e-15);
        double logarithm = ratio < 1e-5 ? ratio * (1 + ratio / 2 + ratio * ratio / 3) : -Math.Log(1 - ratio);
        return logarithm / (conductance * inverseCapacity);
    }

    public static void Exchange(ref Particle a, ref Particle b, double seconds, double contactConductance = 0)
    {
        if (seconds <= 0 || a.Element == Element.Empty || b.Element == Element.Empty) return;
        double difference = b.Temperature - a.Temperature;
        if (difference > 1e-10) ExchangeColdHot(ref a, ref b, seconds, contactConductance, difference);
        else if (difference < -1e-10) ExchangeColdHot(ref b, ref a, seconds, contactConductance, -difference);
    }

    private static void ExchangeColdHot(ref Particle cold, ref Particle hot, double seconds, double contactConductance, double initialDifference)
    {
        for (int n = 0; n < 16 && seconds > 0; n++)
        {
            double difference = n == 0 ? initialDifference : hot.Temperature - cold.Temperature;
            if (difference <= 1e-10) return;
            double ka = LiquidProperties.Conductivity(cold), kb = LiquidProperties.Conductivity(hot);
            // Thermal resistances of the two half cells in series: G = k_harmonic A / dx.
            double conductance = contactConductance > 0 ? contactConductance : 2 * ka * kb / (ka + kb) * World.Depth;
            if (conductance <= 0) return;
            var a = Segment(cold, true); var b = Segment(hot, false);
            double inverseCapacity = 1 / a.Capacity + 1 / b.Capacity;
            double q = Transfer(difference, inverseCapacity, conductance, seconds);
            double limit = Math.Min(a.Boundary - cold.Energy, hot.Energy - b.Boundary);
            bool boundary = limit < q;
            if (boundary) { q = limit; seconds -= TimeToBoundary(q, difference, inverseCapacity, conductance); }
            cold.Energy += q; hot.Energy -= q;
            ResolvePhase(ref cold); ResolvePhase(ref hot);
            if (!boundary) return;
        }
    }

    // Convection with an atmosphere of fixed temperature, using the same latent-heat solver.
    public static double ExchangeWithReservoir(ref Particle p, double temperature, double conductance, double seconds)
    {
        double before = p.Energy;
        for (int n = 0; n < 16 && seconds > 0; n++)
        {
            double difference = temperature - p.Temperature;
            if (Math.Abs(difference) <= 1e-10 || conductance <= 0) break;
            bool heating = difference > 0;
            var segment = Segment(p, heating);
            double inverseCapacity = 1 / segment.Capacity;
            double q = Transfer(Math.Abs(difference), inverseCapacity, conductance, seconds);
            double limit = Math.Abs(segment.Boundary - p.Energy);
            bool boundary = limit < q;
            if (boundary) { q = limit; seconds -= TimeToBoundary(q, Math.Abs(difference), inverseCapacity, conductance); }
            p.Energy += heating ? q : -q;
            ResolvePhase(ref p);
            if (!boundary) break;
        }
        return p.Energy - before;
    }
}
