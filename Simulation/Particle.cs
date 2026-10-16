namespace Powder;

public struct Particle
{
    public Element Element;
    public Element MeltOrigin;
    public double Mass;          // kg, retained during movement and phase changes
    public double Energy;        // J, includes phase-transition energy
    public double DissolvedSalt; // kg, included in Mass
    public double EnthalpyOffset; // J/kg, energy reference retained when sand becomes glass
    public double VelocityY;     // cells / second
    public double Travel;        // fractional cells
    public float Life;
    public uint Stamp;
    public byte Variation;

    // Sensible heat capacity. Latent intervals are handled separately by the enthalpy solver.
    public readonly double HeatCapacity => Math.Max(1e-12,
        (Mass - DissolvedSalt) * Materials.Get(Element).SpecificHeat + DissolvedSalt * Materials.Get(Element.Salt).SpecificHeat);

    public readonly double Temperature => Thermodynamics.Temperature(this);
}
