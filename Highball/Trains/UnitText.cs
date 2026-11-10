using System.Globalization;
using RailWerkz.Core.Settings;

namespace Highball.Trains;

/// SI quantities as the player reads them, in the units chosen in Settings.
public static class UnitText
{
    private static bool Imperial => Settings.Current.Units == UnitSystem.Imperial;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const double FeetPerMetre = 3.28084;
    private const double KgPerShortTon = 907.18474;
    private const double WattsPerHorsepower = 745.69987;
    private const double NewtonsPerPoundForce = 4.4482216;

    public static string Length(double metres) => Imperial
        ? (metres * FeetPerMetre).ToString(metres * FeetPerMetre < 100.0 ? "0.0" : "#,0", Invariant) + " ft"
        : metres.ToString(metres < 100.0 ? "0.0" : "#,0", Invariant) + " m";

    public static string Mass(double kg) => Imperial
        ? (kg / KgPerShortTon).ToString("#,0", Invariant) + " tons"
        : (kg / 1000.0).ToString("#,0", Invariant) + " t";

    public static string Power(double watts) => Imperial
        ? (watts / WattsPerHorsepower).ToString("#,0", Invariant) + " hp"
        : (watts / 1000.0).ToString("#,0", Invariant) + " kW";

    public static string Force(double newtons) => Imperial
        ? (newtons / NewtonsPerPoundForce).ToString("#,0", Invariant) + " lbf"
        : (newtons / 1000.0).ToString("#,0", Invariant) + " kN";

    /// A liquid's volume, from litres: US gallons, or litres.
    public static string Volume(double litres) => Imperial
        ? (litres / 3.785411784).ToString("#,0", Invariant) + " gal"
        : litres.ToString("#,0", Invariant) + " L";

    /// Sand, from cubic metres: cubic feet, or cubic metres.
    public static string SandVolume(double cubicMetres) => Imperial
        ? (cubicMetres / 0.0283168).ToString("0.0", Invariant) + " cu ft"
        : cubicMetres.ToString("0.00", Invariant) + " m³";

    /// A rate of burning fuel, from litres an hour: gallons an hour, or litres an hour.
    public static string FuelRate(double litresPerHour) => Imperial
        ? (litresPerHour / 3.785411784).ToString("#,0", Invariant) + " gal/h"
        : litresPerHour.ToString("#,0", Invariant) + " L/h";

    public static string Speed(double metresPerSecond) => Imperial
        ? (metresPerSecond * 2.2369363).ToString("0", Invariant) + " mph"
        : (metresPerSecond * 3.6).ToString("0", Invariant) + " km/h";

    public const double KpaPerPsi = 6.894757;

    /// An air pressure, from gauge psi (what the air brake works in): psi, or kPa.
    public static string Pressure(double psi) => Imperial
        ? psi.ToString("0", Invariant) + " psi"
        : (psi * KpaPerPsi).ToString("0", Invariant) + " kPa";

    /// Gauge psi in the units shown, and their name, for scales.
    public static double PressureValue(double psi) => Imperial ? psi : psi * KpaPerPsi;
    public static string PressureUnit => Imperial ? "psi" : "kPa";

    /// A big force as couplers are rated: thousands of pounds (klbf), or kN.
    public static string ForceK(double newtons) => Imperial
        ? (newtons / NewtonsPerPoundForce / 1000.0).ToString("0", Invariant) + " klbf"
        : (newtons / 1000.0).ToString("0", Invariant) + " kN";

    public static double ForceKValue(double newtons) => Imperial ? newtons / NewtonsPerPoundForce / 1000.0 : newtons / 1000.0;
    public static string ForceKUnit => Imperial ? "klbf" : "kN";

    /// Power for each unit of weight: what decides how a train climbs.
    public static string PowerToWeight(double watts, double kg) => kg <= 0.0
        ? "-"
        : Imperial
            ? (watts / WattsPerHorsepower / (kg / KgPerShortTon)).ToString("0.00", Invariant) + " hp/ton"
            : (watts / 1000.0 / (kg / 1000.0)).ToString("0.00", Invariant) + " kW/t";
}
