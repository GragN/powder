namespace Powder;

public sealed partial class World
{
    private void Move(int i, int x, int y)
    {
        Element e = Cells[i].Element;
        MatterState state = Materials.Get(e).State;
        if (state is MatterState.Empty or MatterState.Solid) return;
        bool gas = state is MatterState.Gas or MatterState.Flame;
        ref var p = ref Cells[i];
        double dx, dy;
        if (gas && AtmosphereEnabled)
        {
            // Gas markers are entrained by the carrier flow, rather than moving upward on a timer.
            var flow = Air.VelocityAtFine(x, y);
            double targetX = flow.X / CellSize, targetY = flow.Y / CellSize;
            const double mixingTime = .04;
            double decay = Math.Exp(-FixedDelta / mixingTime);
            dx = targetX * FixedDelta + (p.VelocityX - targetX) * mixingTime * (1 - decay);
            dy = targetY * FixedDelta + (p.VelocityY - targetY) * mixingTime * (1 - decay);
            p.VelocityX = targetX + (p.VelocityX - targetX) * decay;
            p.VelocityY = targetY + (p.VelocityY - targetY) * decay;
        }
        else
        {
            dx = p.VelocityX * FixedDelta;
            double acceleration = gas ? 0 : Gravity / CellSize;
            dy = p.VelocityY * FixedDelta + .5 * acceleration * FixedDelta * FixedDelta;
            p.VelocityY += acceleration * FixedDelta;
        }
        double totalX = p.TravelX + dx, totalY = p.Travel + dy;
        double stepsX = Math.Truncate(totalX), stepsY = Math.Truncate(totalY);
        p.TravelX = totalX - stepsX; p.Travel = totalY - stepsY;
        int sx = Math.Sign(stepsX), sy = Math.Sign(stepsY);
        double remainingX = Math.Abs(stepsX), remainingY = Math.Abs(stepsY);
        double timeX = remainingX > 0 ? .5 / remainingX : double.PositiveInfinity;
        double timeY = remainingY > 0 ? .5 / remainingY : double.PositiveInfinity;
        double incrementX = remainingX > 0 ? 1 / remainingX : 0;
        double incrementY = remainingY > 0 ? 1 / remainingY : 0;
        bool blockedDown = false;
        // Interleave grid crossings along the path; never jump over a material wall.
        while (remainingX > 0 || remainingY > 0)
        {
            bool horizontal = remainingX > 0 && (remainingY == 0 || timeX <= timeY);
            int xx = x + (horizontal ? sx : 0), yy = y + (horizontal ? 0 : sy);
            if (TryMove(i, xx, yy, rising: !horizontal && sy < 0))
            {
                if (!Inside(xx, yy)) return;
                x = xx; y = yy; i = y * Width + x;
                if (horizontal) { remainingX--; timeX += incrementX; }
                else { remainingY--; timeY += incrementY; }
            }
            else if (horizontal)
            {
                remainingX = 0; Cells[i].VelocityX = 0; Cells[i].TravelX = 0;
            }
            else
            {
                remainingY = 0; Cells[i].VelocityY = 0; Cells[i].Travel = 0;
                blockedDown = sy > 0;
            }
        }
        if (gas || !blockedDown) return;
        // A one-cell surface height difference supplies gravitational head. Viscous
        // mobility (thin-layer estimate) is bounded by the gravity-wave speed.
        // This remains a whole-cell free surface, while the bulk flow is continuous.
        if (state == MatterState.Liquid)
        {
            double viscosity = LiquidProperties.KinematicViscosity(Cells[i]);
            double surfaceSpeed = Math.Min(Math.Sqrt(Gravity * CellSize), Gravity * CellSize * CellSize / (3 * viscosity));
            if (random.NextDouble() >= surfaceSpeed * FixedDelta / CellSize) return;
        }
        int direction = random.Next(2) == 0 ? -1 : 1;
        if (TryMove(i, x + direction, y + 1) || TryMove(i, x - direction, y + 1)) return;
        if (state != MatterState.Liquid) return;
        if (!TryMove(i, x + direction, y)) TryMove(i, x - direction, y);
    }
}
