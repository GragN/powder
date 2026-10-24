namespace Powder;

// Pure managed appearance rules, shared by the renderer and headless checks.
public static class ParticleAppearance
{
    private static readonly Rgb[] VariationColors = CreateVariationColors();

    public static Rgb VariedColor(Element element, byte variation)
        => VariationColors[(int)element * 256 + variation];

    // A parcel is a visual marker for a variable amount of vapor. Optical depth
    // grows with mass: dilute evaporation is faint, and boiling parcels are dense.
    // Temperature deliberately does not affect opacity: cooling cannot erase mass.
    public static float VaporOpacity(in Particle particle)
    {
        if (particle.Element is not (Element.Steam or Element.Vapor)) return 1;
        if (particle.Mass <= 0) return 0;
        double referenceMass = Materials.Get(particle.Element).Density * World.CellVolume;
        double opticalDepth = particle.Mass / referenceMass;
        return (float)(1 - Math.Exp(-opticalDepth));
    }

    // Condensate can switch storage layers within a tick or when it grows past
    // the resolution threshold. Its appearance must not depend on that layer.
    // Fractional drops fill only part of a pixel; full water/ice cells stay opaque.
    public static float Opacity(in Particle particle)
    {
        if (particle.Element is Element.Water or Element.Ice)
        {
            double fullMass = Materials.Get(particle.Element).Density * World.CellVolume;
            return (float)Math.Clamp(particle.Mass / fullMass, 0, 1);
        }
        return VaporOpacity(particle);
    }

    public static float DispersedOpacity(in Particle particle) => Opacity(particle);

    private static Rgb[] CreateVariationColors()
    {
        var colors = new Rgb[Materials.All.Length * 256];
        foreach (var material in Materials.All)
            for (int variation = 0; variation < 256; variation++)
            {
                float multiplier = .86f + variation / 255f * .24f;
                var c = material.Color;
                colors[(int)material.Id * 256 + variation] = new(
                    (byte)Math.Clamp(c.R * multiplier, 0, 255),
                    (byte)Math.Clamp(c.G * multiplier, 0, 255),
                    (byte)Math.Clamp(c.B * multiplier, 0, 255));
            }
        return colors;
    }
}
