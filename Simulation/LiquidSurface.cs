namespace Powder;

public sealed partial class World
{
    // A volume-aware free surface. Move mass/enthalpy down a pressure head instead
    // of randomly throwing full liquid cells through a vapor-filled channel.
    private void FlowLiquid(int index, int x, int y)
    {
        if (Cells[index].Element == Element.Empty) return;
        int kind = LiquidProperties.Kind(Cells[index]);
        if (kind == 0) return;
        bool Receives(int xx, int yy)
        {
            if (!Inside(xx, yy)) return yy < Height;
            ref readonly var other = ref At(xx, yy);
            return other.Element == Element.Empty || LiquidProperties.Kind(other) == kind
                || Materials.Get(other.Element).State is MatterState.Gas or MatterState.Flame;
        }
        // Completely filled interior cells require no viscosity evaluation or surface work.
        double rest = LiquidMassPerCell(Cells[index]);
        bool needsWork = Cells[index].Mass > rest * 1.00001;
        for (int k = -1; k <= 1 && !needsWork; k++)
            if (Receives(x + k, y + 1) && (!Inside(x + k, y + 1) || At(x + k, y + 1).Mass < rest * .99999)) needsWork = true;
        if (!needsWork)
            for (int sign = -1; sign <= 1; sign += 2)
                if (Receives(x + sign, y) && (!Inside(x + sign, y) || At(x + sign, y).Mass < Cells[index].Mass - rest * .00001))
                { needsWork = true; break; }
        if (!needsWork) return;
        double nu = LiquidProperties.KinematicViscosity(Cells[index]);
        double speed = Math.Min(Math.Sqrt(Gravity * CellSize), Gravity * CellSize * CellSize / (3 * nu));
        double mobility = Math.Min(1, speed * FixedDelta / CellSize);
        if (mobility < 1e-10) return;
        TransferLiquid(index, x, y + 1, double.PositiveInfinity, mobility);
        if (Cells[index].Element == Element.Empty) return;
        int direction = (Tick & 1) == 0 ? -1 : 1;
        for (int k = 0; k < 2 && Cells[index].Element != Element.Empty; k++)
        {
            int sign = k == 0 ? direction : -direction;
            // No diagonal shortcut through the corner of a solid wall.
            if (Receives(x + sign, y)) TransferLiquid(index, x + sign, y + 1, double.PositiveInfinity, mobility);
            if (Cells[index].Element == Element.Empty) break;
            int xx = x + sign;
            if (!Receives(xx, y)) continue;
            // Surface tension is unresolved, but touching subcell drops should not
            // remain two half-filled cells merely because their levels are equal.
            if (TryCoalesceLiquid(index, xx, y)) return;
            double otherFill = Inside(xx, y) && LiquidProperties.Kind(At(xx, y)) == kind ? At(xx, y).Mass / LiquidMassPerCell(At(xx, y)) : 0;
            double fill = Cells[index].Mass / rest;
            double amount = Math.Max(0, fill - otherFill) * rest * .5;
            TransferLiquid(index, xx, y, amount, mobility);
        }
        if (Cells[index].Element != Element.Empty && Cells[index].Mass > rest)
            TransferLiquid(index, x, y - 1, Cells[index].Mass - rest, mobility);
    }

    private void TransferLiquid(int from, int x, int y, double requested, double mobility)
    {
        ref var source = ref Cells[from];
        if (source.Element == Element.Empty || y >= Height) return;
        if (!Inside(x, y))
        {
            // An open side admits outflow; the impermeable floor never does.
            if (double.IsPositiveInfinity(requested)) requested = source.Mass;
            double amount = Math.Min(source.Mass, requested * mobility);
            if (amount <= 0) return;
            var escaped = Portion(source, amount);
            source.Mass -= amount; source.Energy -= escaped.Energy; source.DissolvedSalt -= escaped.DissolvedSalt;
            EscapedMass += amount; EscapedEnergy += escaped.Energy;
            if (source.Mass <= 0) source = default;
            return;
        }
        int to = y * Width + x;
        if (to == from) return;
        ref var target = ref Cells[to];
        var state = Materials.Get(target.Element).State;
        if (state is MatterState.Gas or MatterState.Flame)
        {
            if (!IsVapor(target) || !AddBubble(to, target)) return;
            target = default;
        }
        if (target.Element != Element.Empty && LiquidProperties.Kind(target) != LiquidProperties.Kind(source)) return;
        double capacity = target.Element == Element.Empty ? LiquidMassPerCell(source) : LiquidMassPerCell(target);
        double available = Math.Max(0, capacity - target.Mass);
        double amountToMove = Math.Min(source.Mass, Math.Min(available, requested) * mobility);
        if (amountToMove <= capacity * 1e-10) return;
        // A new visible 2 mm parcel needs resolved volume. Do not turn a molecular
        // film into a full-cell obstacle/pressure domain across the entire vessel.
        if (target.Element == Element.Empty && amountToMove < capacity * .05) return;
        // Put a rounding-sized remainder into the receiver rather than deleting it.
        if (source.Mass - amountToMove < capacity * 1e-8) amountToMove = source.Mass;
        var parcel = Portion(source, amountToMove);
        parcel.Stamp = stamp;
        if (target.Element == Element.Empty) target = parcel;
        else Merge(ref target, parcel);
        source.Mass -= amountToMove; source.Energy -= parcel.Energy; source.DissolvedSalt -= parcel.DissolvedSalt;
        if (source.Mass <= 0) source = default;
        Thermodynamics.ResolvePhase(ref target);
    }
}
