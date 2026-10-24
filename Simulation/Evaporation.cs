namespace Powder;

public static class WaterEvaporation
{
    // Diffusion across a 2 mm surface film into dry carrier air. Only temperature
    // is variable: no ambient pressure, humidity or wind field is sampled.
    // Buck's saturation curve supplies the strongly temperature-dependent density.
    public static double MassFlux(double temperature)
    {
        double t = Math.Clamp(temperature, 0, 100), kelvin = t + 273.15;
        double saturation = 611.21 * Math.Exp((18.678 - t / 234.5) * t / (257.14 + t));
        double density = saturation / (461.52 * kelvin);
        double diffusivity = 2.5e-5 * Math.Pow(kelvin / 293.15, 1.81);
        return diffusivity * density / World.CellSize;
    }
}

public sealed partial class World
{
    private void EvaporateDrop(int index, ref Particle drop)
    {
        if (drop.Element != Element.Water || !SubcellDrop(drop)
            || Cells[index].Element is not (Element.Empty or Element.Steam)) return;
        double temperature = drop.Temperature, gasH = Thermodynamics.WaterVaporEnthalpy(temperature);
        double diameter = Math.Cbrt(6 * drop.Mass / (Math.PI * LiquidProperties.Density(drop)));
        double amount = Math.Min(WaterEvaporation.MassFlux(temperature) * Math.PI * diameter * diameter * FixedDelta,
            Math.Min(drop.Mass, Math.Max(0, drop.Energy) / gasH));
        if (amount <= 1e-18) return;
        var vapor = new Particle
        {
            Element = Element.Steam, MeltOrigin = Element.Water, Mass = amount, Energy = amount * gasH,
            CondensationOffset = 100 - Math.Clamp(temperature, 0, 100), Variation = drop.Variation,
            VelocityX = drop.VelocityX, VelocityY = drop.VelocityY, Stamp = stamp
        };
        if (Cells[index].Element == Element.Empty) Cells[index] = vapor;
        else Merge(ref Cells[index], vapor);
        drop.Mass -= amount; drop.Energy -= vapor.Energy;
        if (drop.Mass <= 0) drop = default;
        else Thermodynamics.ResolvePhase(ref drop);
    }

    private bool DisperseCondensate(int index)
    {
        ref var drop = ref Cells[index];
        if (!SubcellDrop(drop)) return false;
        int x = index % Width, y = index / Width;
        // Return condensate touching a body of water directly to that body.
        for (int face = 0; face < 4; face++)
        {
            int xx = x + (face == 0 ? -1 : face == 1 ? 1 : 0), yy = y + (face == 2 ? -1 : face == 3 ? 1 : 0);
            if (!Inside(xx, yy) || Cells[yy * Width + xx].Element != Element.Water) continue;
            Merge(ref Cells[yy * Width + xx], drop); drop = default;
            Thermodynamics.ResolvePhase(ref Cells[yy * Width + xx]);
            return true;
        }
        if (!AddBubble(index, drop)) return false;
        drop = default;
        return true;
    }

    private void EvaporateSurface(int index)
    {
        ref var water = ref Cells[index];
        int x = index % Width, y = index / Width;
        Span<int> destinations = stackalloc int[4];
        int count = 0;
        for (int face = 0; face < 4; face++)
        {
            int xx = x + (face == 0 ? -1 : face == 1 ? 1 : 0);
            int yy = y + (face == 2 ? -1 : face == 3 ? 1 : 0);
            if (yy >= Height) continue; // The bottom is an impermeable floor.
            if (!Inside(xx, yy)) { destinations[count++] = -1; continue; }
            int to = yy * Width + xx;
            var state = Materials.Get(Cells[to].Element).State;
            if (state is MatterState.Empty or MatterState.Gas or MatterState.Flame)
                destinations[count++] = to;
        }
        if (count == 0) return;

        double temperature = water.Temperature;
        double gasEnthalpy = Thermodynamics.WaterVaporEnthalpy(temperature);
        double fill = Math.Min(1, water.Mass / LiquidMassPerCell(water));
        double amountPerFace = WaterEvaporation.MassFlux(temperature) * CellSize * Depth
            * Math.Pow(fill, 2.0 / 3) * FixedDelta;
        for (int face = 0; face < count && water.Element == Element.Water; face++)
        {
            // Evaporation draws both the departing mass's sensible heat and its
            // latent heat from the liquid. The remainder cools, and can freeze.
            // Never consume nonvolatile salt or cool the remainder past ice at 0 C.
            double amount = Math.Min(amountPerFace, Math.Min(water.Mass - water.DissolvedSalt,
                Math.Max(0, water.Energy) / gasEnthalpy));
            if (amount <= 1e-18) break;
            var vapor = new Particle
            {
                Element = Element.Steam, MeltOrigin = Element.Water, Mass = amount,
                Energy = amount * gasEnthalpy, CondensationOffset = 100 - Math.Clamp(temperature, 0, 100),
                VelocityX = water.VelocityX, VelocityY = water.VelocityY,
                Variation = water.Variation, Stamp = stamp
            };
            int to = destinations[face];
            if (to < 0) { EscapedMass += amount; EscapedEnergy += vapor.Energy; }
            else if (Cells[to].Element == Element.Empty) Cells[to] = vapor;
            else if (SameVapor(Cells[to], vapor)) Merge(ref Cells[to], vapor);
            else if (!AddBubble(to, vapor)) continue;
            water.Mass -= amount; water.Energy -= vapor.Energy;
            if (water.Mass <= 0) water = default;
            else Thermodynamics.ResolvePhase(ref water);
        }
    }

    // A collision between two fractional drops makes one parcel when their total
    // volume fits. A bulk liquid cell still uses the pressure/free-surface solvers.
    private bool TryCoalesceLiquid(int from, int x, int y)
    {
        if (!Inside(x, y)) return false;
        int to = y * Width + x;
        ref var source = ref Cells[from]; ref var target = ref Cells[to];
        int kind = LiquidProperties.Kind(source);
        if (kind == 0 || kind != LiquidProperties.Kind(target)) return false;
        double fill = source.Mass / LiquidMassPerCell(source) + target.Mass / LiquidMassPerCell(target);
        if (fill > 1 + 1e-10) return false;
        Merge(ref target, source); source = default;
        target.Stamp = stamp;
        Thermodynamics.ResolvePhase(ref target);
        return true;
    }
}
