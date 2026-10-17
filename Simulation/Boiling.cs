namespace Powder;

public sealed partial class World
{
    // Unresolved gas parcels inside a liquid. They do not replace an entire dense
    // liquid cell: a small amount of latent energy can create a small bubble now.
    // This is a dispersed-phase model at ambient pressure, not a compressible VOF solver.
    public Particle[] Bubbles { get; private set; } = [];
    private List<int> bubbleIndices = [];
    private Particle[] bubbleScratch = [];
    private bool[] bubbleListed = [];
    public ReadOnlySpan<int> BubbleLocations => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bubbleIndices);
    private List<int> nextBubbleIndices = [];
    private readonly List<int> phaseCandidates = [];

    private static bool IsVapor(in Particle p) => p.Element is Element.Steam or Element.Vapor;
    private static Element Substance(in Particle p) => Materials.IsWater(p.Element) ? Element.Water
        : p.Element is Element.Lava or Element.Vapor ? p.MeltOrigin : p.Element;
    private static bool SameVapor(in Particle a, in Particle b) => IsVapor(a) && IsVapor(b) && Substance(a) == Substance(b)
        && Math.Abs(a.EnthalpyOffset - b.EnthalpyOffset) < 1e-8;

    private static double LiquidMassPerCell(in Particle p)
    {
        Element origin = Substance(p);
        double salt = Materials.IsWater(p.Element) ? p.DissolvedSalt / p.Mass : 0;
        return Materials.Get(origin).Density * CellVolume / Math.Max(.01, 1 - salt);
    }

    private bool AddBubble(int index, in Particle parcel)
    {
        if (parcel.DissolvedSalt != 0) return false;
        ref var target = ref Bubbles[index];
        if (target.Element != Element.Empty && !SameVapor(target, parcel)) return false;
        if (target.Element == Element.Empty)
        {
            target = parcel; target.Stamp = stamp;
            if (!bubbleListed[index]) { bubbleIndices.Add(index); bubbleListed[index] = true; }
        }
        else Merge(ref target, parcel);
        return true;
    }

    // Mix extensive quantities, including the reference enthalpy and momentum.
    private static void Merge(ref Particle receiver, in Particle donor)
    {
        double total = receiver.Mass + donor.Mass;
        receiver.EnthalpyOffset = (receiver.EnthalpyOffset * receiver.Mass + donor.EnthalpyOffset * donor.Mass) / total;
        receiver.VelocityX = (receiver.VelocityX * receiver.Mass + donor.VelocityX * donor.Mass) / total;
        receiver.VelocityY = (receiver.VelocityY * receiver.Mass + donor.VelocityY * donor.Mass) / total;
        receiver.Mass = total; receiver.Energy += donor.Energy; receiver.DissolvedSalt += donor.DissolvedSalt;
    }

    private static Particle Portion(in Particle source, double mass)
    {
        var result = source;
        double fraction = mass / source.Mass;
        result.Mass = mass; result.Energy *= fraction; result.DissolvedSalt *= fraction;
        return result;
    }

    // Called after thermal exchange, before bulk advection. Any newly supplied latent
    // energy can form vapor; the rest of the liquid stays at its saturation temperature.
    private void ReleaseVapor()
    {
        phaseCandidates.Clear();
        for (int k = 0; k < activeCount; k++)
        {
            int i = activeCells[k];
            SeparateNonvolatileSalt(i);
            ref readonly var p = ref Cells[i];
            if (Materials.Get(p.Element).State == MatterState.Liquid) phaseCandidates.Add(i);
        }
        foreach (int i in phaseCandidates)
        {
            ref var p = ref Cells[i];
            Element origin = Substance(p);
            if (origin != Element.Water && !VaporProperties.Supports(origin)) continue;
            double waterMass = p.Mass - p.DissolvedSalt;
            double liquidH = origin == Element.Water ? Thermodynamics.BoilingLiquid : VaporProperties.BoilingLiquid(origin);
            double gasH = origin == Element.Water ? Thermodynamics.BoilingGas : VaporProperties.BoilingGas(origin);
            double latent = gasH - liquidH;
            double liquidEnergy = waterMass * (p.EnthalpyOffset + liquidH)
                + p.DissolvedSalt * Materials.Get(Element.Salt).SpecificHeat * VaporProperties.BoilingPoint(origin);
            double excess = p.Energy - liquidEnergy;
            if (excess <= 1e-11) continue;
            double amount = Math.Min(waterMass, excess / latent);
            // Parcels can represent multiple unresolved bubbles. Bound mass per emission,
            // not the latent heat or boiling temperature.
            amount = Math.Min(amount, LiquidMassPerCell(p) * .025);
            if (amount < 1e-14) continue;
            var vapor = new Particle
            {
                Element = origin == Element.Water ? Element.Steam : Element.Vapor,
                MeltOrigin = origin, Mass = amount, Energy = amount * (p.EnthalpyOffset + gasH),
                EnthalpyOffset = p.EnthalpyOffset, Variation = p.Variation, Stamp = stamp
            };
            if (!AddBubble(i, vapor)) continue;
            p.Mass -= amount; p.Energy -= vapor.Energy;
            // Salt is nonvolatile. It remains in the solution, not in the new vapor parcel.
            if (p.Mass <= 1e-14) p = default;
            else { Thermodynamics.ResolvePhase(ref p); SeparateNonvolatileSalt(i); }
        }
    }

    private void SeparateNonvolatileSalt(int index)
    {
        ref var p = ref Cells[index];
        if (p.Element != Element.Steam || p.DissolvedSalt <= 0) return;
        double saltMass = p.DissolvedSalt;
        double saltEnergy = saltMass * Materials.Get(Element.Salt).SpecificHeat * p.Temperature;
        double vaporMass = p.Mass - saltMass;
        var vapor = p; vapor.Mass = vaporMass; vapor.Energy -= saltEnergy; vapor.DissolvedSalt = 0;
        if (vaporMass > 1e-14 && !AddBubble(index, vapor)) return;
        p.Element = p.MeltOrigin = Element.Salt; p.Mass = saltMass; p.Energy = saltEnergy;
        p.DissolvedSalt = p.EnthalpyOffset = 0;
    }

    private bool DepositCondensate(int index, in Particle condensate)
    {
        ref var liquid = ref Cells[index];
        if (liquid.Element == Element.Empty)
        {
            liquid = condensate; liquid.Stamp = stamp;
            if (liquid.RigidId == -2) Solids.QueueSolid(index);
            return true;
        }
        if (Substance(liquid) != Substance(condensate) || Materials.Get(liquid.Element).State != MatterState.Liquid)
            return false;
        Merge(ref liquid, condensate); Thermodynamics.ResolvePhase(ref liquid);
        if (liquid.RigidId == -2) Solids.QueueSolid(index);
        return true;
    }

    private void CondenseBubble(int index, ref Particle bubble)
    {
        if (!IsVapor(bubble))
        {
            if (DepositCondensate(index, bubble)) bubble = default;
            return;
        }
        Element origin = Substance(bubble);
        double liquidH = (origin == Element.Water ? Thermodynamics.BoilingLiquid : VaporProperties.BoilingLiquid(origin)) + bubble.EnthalpyOffset;
        double gasH = (origin == Element.Water ? Thermodynamics.BoilingGas : VaporProperties.BoilingGas(origin)) + bubble.EnthalpyOffset;
        double deficit = bubble.Mass * gasH - bubble.Energy;
        if (deficit <= 1e-11) return;
        double amount = Math.Min(bubble.Mass, deficit / (gasH - liquidH));
        if (amount < 1e-14) return;
        var condensed = bubble;
        condensed.Element = origin == Element.Water ? Element.Water : origin == Element.Oil ? Element.Oil : Element.Lava;
        condensed.Mass = amount; condensed.Energy = amount * liquidH;
        condensed.VelocityX = condensed.VelocityY = condensed.Travel = condensed.TravelX = 0;
        if (!DepositCondensate(index, condensed)) return;
        if (amount >= bubble.Mass * (1 - 1e-12)) bubble = default;
        else { bubble.Mass -= amount; bubble.Energy -= condensed.Energy; Thermodynamics.ResolvePhase(ref bubble); }
    }

    private bool HasLiquidNeighbor(int x, int y)
    {
        for (int k = 0; k < 4; k++)
        {
            int xx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), yy = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
            if (Inside(xx, yy) && Materials.Get(At(xx, yy).Element).State == MatterState.Liquid) return true;
        }
        return false;
    }

    private static double BubbleDiameter(in Particle parcel)
    {
        double density = Math.Max(.01, LiquidProperties.Density(parcel));
        double diameter = Math.Cbrt(6 * parcel.Mass / (Math.PI * density));
        // A parcel can represent several unresolved bubbles. Their sizes vary,
        // rather than every source emitting a synchronized row of identical markers.
        return Math.Clamp(diameter, CellSize * .2, CellSize * 1.5) * (.8 + .4 * parcel.Variation / 255);
    }

    private (double X, double Y, double Relaxation) BubbleVelocity(int x, int y, in Particle parcel)
    {
        double density = 0, viscosity = 0; int wet = 0;
        for (int k = 0; k < 5; k++)
        {
            int xx = x + (k == 1 ? -1 : k == 2 ? 1 : 0), yy = y + (k == 3 ? -1 : k == 4 ? 1 : 0);
            if (!Inside(xx, yy)) continue;
            ref readonly var p = ref At(xx, yy);
            if (Materials.Get(p.Element).State != MatterState.Liquid) continue;
            density += LiquidProperties.Density(p); viscosity += LiquidProperties.KinematicViscosity(p); wet++;
        }
        if (wet == 0)
        {
            var air = AtmosphereEnabled ? Air.VelocityAtFine(x, y) : (X: 0.0, Y: 0.0);
            return (air.X / CellSize, air.Y / CellSize, .04);
        }
        density /= wet; viscosity /= wet;
        // Buoyancy balanced against viscous/inertial drag for an unresolved mm bubble.
        // Distinguishes a bubble in water from the carrier air outside the liquid.
        double delta = 1 - LiquidProperties.Density(parcel) / density, diameter = BubbleDiameter(parcel);
        double slip = Math.Sign(delta) * Math.Min(Math.Sqrt(4 * Gravity * diameter * Math.Abs(delta) / 3),
            Gravity * Math.Abs(delta) * diameter * diameter / (18 * viscosity));
        var flow = Liquids.VelocityAtFine(x, y);
        return (flow.X / CellSize, (flow.Y - slip) / CellSize, .03);
    }

    private void StepBubbles()
    {
        // Separate old/new layers advance every mass parcel exactly once. Clear only
        // touched cells; a mostly empty bubble layer has no full-grid update cost.
        foreach (int index in nextBubbleIndices) bubbleScratch[index] = default;
        nextBubbleIndices.Clear();
        foreach (int index in bubbleIndices)
        {
            if (Bubbles[index].Element == Element.Empty) continue;
            var parcel = Bubbles[index]; Bubbles[index] = default;
            int x = index % Width, y = index / Width;
            ref var body = ref Cells[index];
            if (body.Element != Element.Empty)
            {
                double g = 2 * Math.PI * LiquidProperties.Conductivity(body) * BubbleDiameter(parcel);
                Thermodynamics.Exchange(ref parcel, ref body, FixedDelta, g);
            }
            CondenseBubble(index, ref parcel);
            if (parcel.Element == Element.Empty) continue;
            var target = BubbleVelocity(x, y, parcel);
            double decay = Math.Exp(-FixedDelta / target.Relaxation);
            double dx = target.X * FixedDelta + (parcel.VelocityX - target.X) * target.Relaxation * (1 - decay);
            double dy = target.Y * FixedDelta + (parcel.VelocityY - target.Y) * target.Relaxation * (1 - decay);
            parcel.VelocityX = target.X + (parcel.VelocityX - target.X) * decay;
            parcel.VelocityY = target.Y + (parcel.VelocityY - target.Y) * decay;
            double tx = parcel.TravelX + dx, ty = parcel.Travel + dy;
            int sx = (int)Math.Truncate(tx), sy = (int)Math.Truncate(ty);
            parcel.TravelX = tx - sx; parcel.Travel = ty - sy;
            int last = index, xx = x, yy = y;
            int remainingX = Math.Abs(sx), remainingY = Math.Abs(sy);
            int stepX = Math.Sign(sx), stepY = Math.Sign(sy);
            double timeX = remainingX == 0 ? double.PositiveInfinity : .5 / remainingX;
            double timeY = remainingY == 0 ? double.PositiveInfinity : .5 / remainingY;
            double incrementX = remainingX == 0 ? 0 : 1.0 / remainingX;
            double incrementY = remainingY == 0 ? 0 : 1.0 / remainingY;
            while (remainingX > 0 || remainingY > 0)
            {
                bool horizontal = remainingX > 0 && (remainingY == 0 || timeX <= timeY);
                int nx = xx + (horizontal ? stepX : 0), ny = yy + (horizontal ? 0 : stepY);
                if (ny >= Height) break;
                if (!Inside(nx, ny)) { EscapedMass += parcel.Mass; EscapedEnergy += parcel.Energy; parcel = default; break; }
                int to = ny * Width + nx;
                var state = Materials.Get(Cells[to].Element).State;
                if (state is MatterState.Solid or MatterState.Powder
                    // Do not enter a parcel's original cell before it has advanced:
                    // it may condense or hit a wall and need to remain there.
                    || Bubbles[to].Element != Element.Empty
                    || bubbleScratch[to].Element != Element.Empty && !SameVapor(bubbleScratch[to], parcel))
                { parcel.VelocityX = parcel.VelocityY = parcel.TravelX = parcel.Travel = 0; break; }
                last = to; xx = nx; yy = ny;
                if (horizontal) { remainingX--; timeX += incrementX; }
                else { remainingY--; timeY += incrementY; }
            }
            if (parcel.Element == Element.Empty) continue;
            // Once outside liquid, the parcel joins the normal atmosphere marker layer.
            if (Cells[last].Element == Element.Empty)
            {
                Cells[last] = parcel; Cells[last].Stamp = stamp; continue;
            }
            if (SameVapor(Cells[last], parcel)) { Merge(ref Cells[last], parcel); continue; }
            if (bubbleScratch[last].Element == Element.Empty)
            { bubbleScratch[last] = parcel; nextBubbleIndices.Add(last); }
            else Merge(ref bubbleScratch[last], parcel);
        }
        foreach (int index in bubbleIndices) bubbleListed[index] = false;
        foreach (int index in nextBubbleIndices) bubbleListed[index] = true;
        (Bubbles, bubbleScratch) = (bubbleScratch, Bubbles);
        (bubbleIndices, nextBubbleIndices) = (nextBubbleIndices, bubbleIndices);
    }

    public void RebuildBubbleIndices()
    {
        bubbleIndices.Clear(); Array.Clear(bubbleListed);
        for (int i = 0; i < Bubbles.Length; i++)
            if (Bubbles[i].Element != Element.Empty) { bubbleIndices.Add(i); bubbleListed[i] = true; }
    }
}
