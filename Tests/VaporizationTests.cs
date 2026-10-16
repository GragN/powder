using System.Text.Json.Nodes;

namespace Powder;

public static partial class SelfTests
{
    private static readonly Element[] EvaporatingOrigins =
        [Element.Oil, Element.Stone, Element.Copper, Element.Iron, Element.Glass, Element.Sand];

    private static void RunVaporizationTests()
    {
        Test("each liquid boils at its own normal boiling temperature", () =>
        {
            Near(VaporProperties.BoilingPoint(Element.Water), 100, 0);
            Near(VaporProperties.BoilingPoint(Element.Oil), 350, 0);
            Near(VaporProperties.BoilingPoint(Element.Copper), 2562, 0);
            Near(VaporProperties.BoilingPoint(Element.Iron), 2862, 0);
            Near(VaporProperties.BoilingPoint(Element.Stone), 2800, 0);
            Near(VaporProperties.BoilingPoint(Element.Glass), 2950, 0);
            foreach (var e in EvaporatingOrigins)
            {
                double t = VaporProperties.BoilingPoint(e);
                var p = Make(e, t - 1);
                Check(Materials.Get(p.Element).State == MatterState.Liquid, $"{e} evaporated early");
                p = Make(e, t + 1);
                Check(p.Element == Element.Vapor && p.MeltOrigin == e, $"{e} vapor lost its composition");
                Near(p.Temperature, t + 1, 1e-8);
                Near(p.HeatCapacity / p.Mass, VaporProperties.SpecificHeat(e), 1e-8);
                Check(LiquidProperties.Conductivity(p) < .1 && LiquidProperties.Density(p) < 10, "vapor still has liquid properties");
            }
        });
        Test("the heater pays latent heat and condensation returns the original liquid", () =>
        {
            foreach (var e in EvaporatingOrigins.Append(Element.Water))
            {
                double boil = VaporProperties.BoilingPoint(e), latent = VaporProperties.LatentHeat(e);
                var w = new World(8, 8) { AtmosphereEnabled = false };
                w.Set(4, 4, e, boil - 10);
                double mass = w.At(4, 4).Mass, initial = w.At(4, 4).Energy;
                double cp = w.At(4, 4).HeatCapacity / mass;
                w.HeatBrush(4, 4, 0, 1, mass * (10 * cp + latent * .5));
                Check(Materials.Get(w.At(4, 4).Element).State == MatterState.Liquid, $"{e} vaporized before latent heat was supplied");
                Near(w.At(4, 4).Temperature, boil, 1e-8);
                w.HeatBrush(4, 4, 0, 1, mass * (latent * .5 + VaporProperties.SpecificHeat(e) * 20));
                ref var p = ref w.At(4, 4);
                Check(p.Element == (e == Element.Water ? Element.Steam : Element.Vapor), $"{e} did not evaporate");
                Near(p.Temperature, boil + 20, 1e-8);
                Near(p.Mass, mass, 1e-15); Near(p.Energy - initial, w.ExternalEnergy, 1e-8);
                w.HeatBrush(4, 4, 0, -1, mass * (VaporProperties.SpecificHeat(e) * 20 + latent * .5));
                Check(p.Element == (e == Element.Water ? Element.Steam : Element.Vapor), $"{e} condensed before latent heat was released");
                Near(p.Temperature, boil, 1e-8);
                double target = Thermodynamics.EnergyAtTemperature(p, boil - 25);
                w.HeatBrush(4, 4, 0, -1, p.Energy - target);
                Check(p.Element == (e == Element.Water ? Element.Water : e == Element.Oil ? Element.Oil : Element.Lava), $"{e} condensed as the wrong substance");
                if (e != Element.Water) Check(p.MeltOrigin == e, "condensation changed composition");
                Near(p.Temperature, boil - 25, 1e-8);
                Near(p.Mass, mass, 1e-15); Near(p.Energy - initial, w.ExternalEnergy, 1e-8);
            }
        });
        Test("conductive exchange crosses boiling plateaus without creating energy or overshooting", () =>
        {
            foreach (var e in EvaporatingOrigins)
            {
                double boil = VaporProperties.BoilingPoint(e);
                var cold = Make(e, boil - 10); var hot = Make(e, boil + 10);
                double energy = cold.Energy + hot.Energy;
                Thermodynamics.Exchange(ref cold, ref hot, 100_000);
                Near(cold.Energy + hot.Energy, energy, 1e-8);
                Near(cold.Temperature, boil, 1e-7); Near(hot.Temperature, boil, 1e-7);
            }
        });
        Test("large cooling steps cross vaporization and fusion while silica becomes glass", () =>
        {
            foreach (var e in EvaporatingOrigins)
            {
                var p = Make(e, VaporProperties.BoilingPoint(e) + 100);
                double energy = p.Energy, mass = p.Mass;
                double q = Thermodynamics.ExchangeWithReservoir(ref p, 20, 1000, 30);
                Check(p.Element == (e == Element.Sand ? Element.Glass : e), $"{e} cooled to the wrong phase");
                Near(p.Temperature, 20, 1e-7); Near(p.Energy - energy, q, 1e-8); Near(p.Mass, mass, 1e-15);
                if (e == Element.Sand)
                {
                    double offset = p.EnthalpyOffset;
                    for (int n = 0; n < 3; n++)
                    {
                        p.Energy = Thermodynamics.EnergyAtTemperature(p, 3100); Thermodynamics.ResolvePhase(ref p);
                        Check(p.Element == Element.Vapor && p.MeltOrigin == Element.Glass, "silica vapor changed identity");
                        Near(p.Temperature, 3100, 1e-7);
                        Thermodynamics.ExchangeWithReservoir(ref p, 20, 1000, 30);
                        Check(p.Element == Element.Glass, "silica did not condense as glass");
                        Near(p.Temperature, 20, 1e-7); Near(p.EnthalpyOffset, offset, 1e-10);
                    }
                }
            }
        });
        Test("oil reaches boiling before ignition and overheated oil vapor can burn", () =>
        {
            var w = new World(16, 16) { AtmosphereEnabled = false };
            w.Set(4, 4, Element.Oil, 340); w.Set(10, 4, Element.Oil, 370);
            double energy = w.Statistics().Energy, mass = w.Statistics().Mass;
            w.Step();
            Check(w.Cells.Any(p => p.Element == Element.Oil), "liquid oil ignited before boiling");
            Check(w.Cells.Any(p => p.Element == Element.Vapor && p.MeltOrigin == Element.Oil), "oil vapor could not survive below ignition");
            Near(w.Statistics().Energy, energy, 1e-8); Near(w.ExternalEnergy, 0, 1e-12);
            Near(w.Statistics().Mass, mass, 1e-15);
            w.Clear(); w.Set(8, 8, Element.Oil, 450); w.Step();
            Check(w.Cells.Any(p => p.Element == Element.Fire), "overheated oil vapor did not ignite");
            Check(w.ExternalEnergy >= 0, "ignition discarded vaporization energy");
        });
        Test("vapor exchanges heat with finite air and escaping vapor retains its budget", () =>
        {
            var w = new World(32, 32);
            w.Set(16, 16, Element.Copper, 2600);
            double initial = Budget(w), mass = w.Statistics().Mass;
            for (int n = 0; n < 10; n++) w.Step();
            Check(w.Air.ExcessEnergy > 0, "hot vapor did not heat air");
            Check(w.Air.Heat.All(double.IsFinite), "vapor made atmosphere nonfinite");
            Near(Budget(w), initial, 1e-7); Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
            w.Clear(); w.Set(0, 16, Element.Copper, 2600); w.At(0, 16).VelocityX = -240;
            initial = Budget(w); mass = w.Statistics().Mass; w.Step();
            Check(w.EscapedMass > 0, "vapor did not leave the open edge");
            Near(Budget(w), initial, 1e-7); Near(w.Statistics().Mass + w.EscapedMass, mass, 1e-12);
        });
        Test("vapor saves retain composition, partial condensation and silica enthalpy reference", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"powder-vapor-{Guid.NewGuid()}.json");
            try
            {
                var w = new World(16, 16) { AtmosphereEnabled = false };
                for (int n = 0; n < EvaporatingOrigins.Length; n++)
                {
                    var e = EvaporatingOrigins[n];
                    w.Set(3 + n, 5, e, VaporProperties.BoilingPoint(e) + 20);
                    ref var p = ref w.At(3 + n, 5);
                    p.Energy = p.Mass * (VaporProperties.BoilingLiquid(e) + .5 * VaporProperties.LatentHeat(e));
                    Thermodynamics.ResolvePhase(ref p);
                    Check(p.Element == Element.Vapor, "vapor state not retained inside the condensation plateau");
                }
                w.Set(5, 10, Element.Sand, 1800);
                ref var silica = ref w.At(5, 10);
                Thermodynamics.ExchangeWithReservoir(ref silica, 20, 1000, 30);
                silica.Energy = Thermodynamics.EnergyAtTemperature(silica, 3100); Thermodynamics.ResolvePhase(ref silica);
                WorldFile.Save(w, path); var loaded = WorldFile.Load(path, 16, 16);
                for (int i = 0; i < w.Cells.Length; i++)
                {
                    ref readonly var a = ref w.Cells[i]; ref readonly var b = ref loaded.Cells[i];
                    Check(a.Element == b.Element && a.MeltOrigin == b.MeltOrigin, "saved vapor composition changed");
                    Near(a.Energy, b.Energy, 1e-12); Near(a.Mass, b.Mass, 1e-15);
                    Near(a.EnthalpyOffset, b.EnthalpyOffset, 1e-12); Near(a.Temperature, b.Temperature, 1e-9);
                }
                var data = JsonNode.Parse(File.ReadAllText(path))!;
                data["Cells"]![0]!["Origin"] = (int)Element.Empty;
                File.WriteAllText(path, data.ToJsonString());
                bool rejected = false;
                try { WorldFile.Load(path, 16, 16); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "vapor without a supported substance was accepted");
                w.Clear(); w.Set(5, 5, Element.Oil, 340); WorldFile.Save(w, path);
                data = JsonNode.Parse(File.ReadAllText(path))!;
                data["Version"] = 3; data["Cells"]![0]!["Origin"] = (int)Element.Stone;
                double oldEnergy = w.At(5, 5).Mass * 2000 * 400;
                data["Cells"]![0]!["Energy"] = oldEnergy; File.WriteAllText(path, data.ToJsonString());
                loaded = WorldFile.Load(path, 16, 16);
                Near(loaded.At(5, 5).Energy, oldEnergy, 1e-12);
                Near(loaded.At(5, 5).Temperature, 350, 1e-9);
                Check(loaded.At(5, 5).Element == Element.Oil && loaded.At(5, 5).MeltOrigin == Element.Oil, "older liquid scene did not acquire boiling boundaries");
            }
            finally { File.Delete(path); }
        });
    }
}
