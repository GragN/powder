namespace Powder;

public static partial class SelfTests
{
    private static void AppearanceTests()
    {
        Test("fractional water and ice fade with mass without a resolution threshold flash", () =>
        {
            foreach (var element in new[] { Element.Water, Element.Ice })
            {
                var drop = Make(element, element == Element.Water ? 20 : -20);
                double fullMass = drop.Mass;
                foreach (double fill in new[] { 0.0, .000001, .001, .049, .05, .051, .5, 1.0, 1.5 })
                {
                    drop.Mass = fullMass * fill;
                    Near(ParticleAppearance.Opacity(drop), Math.Min(1, fill), 1e-7);
                }
                drop.Mass = fullMass * .049;
                float before = ParticleAppearance.Opacity(drop);
                drop.Mass = fullMass * .051;
                Check(ParticleAppearance.Opacity(drop) - before < .003,
                    "condensate flashes when crossing the subcell resolution threshold");
            }
        });
        Test("vapor opacity follows parcel mass and survives cooling", () =>
        {
            var vapor = Make(Element.Steam, 120);
            double referenceMass = vapor.Mass;
            vapor.Mass = referenceMass * .01;
            float dilute = ParticleAppearance.VaporOpacity(vapor);
            vapor.Mass = referenceMass;
            float ordinary = ParticleAppearance.VaporOpacity(vapor);
            vapor.Mass = Materials.Get(Element.Water).Density * World.CellVolume * .025;
            float boiling = ParticleAppearance.VaporOpacity(vapor);
            Check(dilute > 0 && dilute < .02, "tiny evaporation parcel is opaque or invisible");
            Check(dilute < ordinary && ordinary < boiling && boiling <= 1,
                "vapor opacity does not increase with mass");
            vapor.Energy *= .2;
            Near(ParticleAppearance.VaporOpacity(vapor), boiling, 0);
            vapor.Mass = 0;
            Near(ParticleAppearance.VaporOpacity(vapor), 0, 0);
        });

        Test("overlapping vapor parcels combine their optical depth", () =>
        {
            var a = Make(Element.Steam, 120);
            a.Mass *= .2;
            var b = a; b.Mass *= 2;
            double alphaA = ParticleAppearance.VaporOpacity(a);
            double alphaB = ParticleAppearance.VaporOpacity(b);
            var merged = a; merged.Mass += b.Mass;
            Near(ParticleAppearance.VaporOpacity(merged), 1 - (1 - alphaA) * (1 - alphaB), 1e-7);
        });
    }
}
