using RailWerkz.Core.Utils;

namespace Highball.World;

/// The colours of a clear sky for a given sun, from light scattering through the air: blue overhead because air scatters
/// blue most, red at sunset because the sun's light has crossed so much air. Run on the CPU whenever the sun moves, for
/// the few colours the sky shader blends between; single scattering only, so twilight comes out a little dark.
public static class Atmosphere
{
    public readonly record struct Colors(float3 Sun, float3 Zenith, float3 Horizon, float3 HorizonTowardsSun, float3 Ground)
    {
        /// How bright the sun and sky make a white surface facing up, which is what a camera would expose for.
        public float Light
        {
            get
            {
                var light = Sun * MathF.Max(SunHeight, 0f) + Zenith * 0.9f + Horizon * 0.45f;
                return light.X * 0.2126f + light.Y * 0.7152f + light.Z * 0.0722f;
            }
        }

        public float SunHeight { get; init; }
    }

    private const float EarthRadius = 6371e3f;
    private const float AirRadius = 6471e3f;
    private const float Eye = 10f;

    /// Light scattered more than once, which this doesn't follow: about as much again as scattered once, and a whiter blue.
    private const float SkyGain = 1.5f;
    private static readonly float3 Bounced = new(0.02f, 0.035f, 0.065f);

    /// The game's sun on a white surface facing it, at the top of the air (see lighting.glsl: no divide by pi).
    private const float SunStrength = 3.4f;

    private static readonly float3 Rayleigh = new(5.8e-6f, 13.5e-6f, 33.1e-6f);
    private const float RayleighHeight = 8000f;
    private const float Mie = 6e-6f;
    private const float MieHeight = 1200f;
    private const float MieShape = 0.76f;
    private static readonly float3 Ozone = new(0.65e-6f, 1.881e-6f, 0.085e-6f);

    /// What's left at night, when no sunlight reaches the air: starlight and airglow, with no moon yet.
    private static readonly float3 NightZenith = new(0.004f, 0.006f, 0.014f);
    private static readonly float3 NightHorizon = new(0.006f, 0.008f, 0.016f);

    public static Colors Compute(float3 sun)
    {
        var flat = MathF.Sqrt(sun.X * sun.X + sun.Z * sun.Z);
        var towards = flat > 1e-4f ? new float3(sun.X / flat, 0f, sun.Z / flat) : new float3(1f, 0f, 0f);
        var across = new float3(-towards.Z, 0f, towards.X);

        // Just over the horizon, since it's the band the eye sees most of.
        const float low = 0.09f;
        var zenith = Look(new float3(0f, 1f, 0f), sun);
        var horizonTowards = Look(MathEx.Normalize(towards + new float3(0f, low, 0f)), sun);
        var horizonAway = (Look(MathEx.Normalize(-towards + new float3(0f, low, 0f)), sun)
            + Look(MathEx.Normalize(across + new float3(0f, low, 0f)), sun)
            + Look(MathEx.Normalize(-across + new float3(0f, low, 0f)), sun)) / 3f;

        var color = Transmittance(new float3(0f, EarthRadius + Eye, 0f), sun) * SunStrength;
        var risen = MathEx.SmoothStep(-0.015f, 0.04f, sun.Y);
        color *= risen;

        // Light that's bounced round the air follows how much of the sky the sun is lighting.
        var bounced = Bounced * MathEx.SmoothStep(-0.12f, 0.25f, sun.Y);
        zenith = zenith * SkyGain + bounced + NightZenith;
        horizonAway = horizonAway * SkyGain + bounced + NightHorizon;
        horizonTowards = horizonTowards * SkyGain + bounced + NightHorizon;

        // Dry ground, lit by the sun and the sky above it.
        var light = color * MathF.Max(sun.Y, 0f) + zenith * 0.9f + horizonAway * 0.45f;
        var ground = new float3(light.X * 0.11f, light.Y * 0.10f, light.Z * 0.085f);

        return new Colors(color, zenith, horizonAway, horizonTowards, ground) { SunHeight = sun.Y };
    }

    /// The light arriving down a line of sight, from sunlight scattered into it by the air along the way.
    private static float3 Look(float3 direction, float3 sun)
    {
        const int steps = 24;
        var start = new float3(0f, EarthRadius + Eye, 0f);
        var length = Exit(start, direction);
        var step = length / steps;

        var mu = MathEx.Dot(direction, sun);
        var rayleighPhase = 3f / (16f * MathF.PI) * (1f + mu * mu);
        var g2 = MieShape * MieShape;
        var miePhase = 3f / (8f * MathF.PI) * ((1f - g2) * (1f + mu * mu)) / ((2f + g2) * MathF.Pow(1f + g2 - 2f * MieShape * mu, 1.5f));

        var total = default(float3);
        var opticalR = 0f;
        var opticalM = 0f;
        var opticalO = 0f;
        for (var i = 0; i < steps; i++)
        {
            var p = start + direction * ((i + 0.5f) * step);
            var height = MathEx.Length(p) - EarthRadius;
            var densityR = MathF.Exp(-height / RayleighHeight);
            var densityM = MathF.Exp(-height / MieHeight);
            opticalR += densityR * step;
            opticalM += densityM * step;
            opticalO += OzoneDensity(height) * step;

            var towardsSun = Hits(p, sun) ? default : Transmittance(p, sun);
            var view = Extinction(opticalR, opticalM, opticalO);
            var scattered = new float3(
                Rayleigh.X * densityR * rayleighPhase + Mie * densityM * miePhase,
                Rayleigh.Y * densityR * rayleighPhase + Mie * densityM * miePhase,
                Rayleigh.Z * densityR * rayleighPhase + Mie * densityM * miePhase);
            total += new float3(scattered.X * view.X * towardsSun.X, scattered.Y * view.Y * towardsSun.Y, scattered.Z * view.Z * towardsSun.Z) * step;
        }

        // The sun's strength in the same units as above: its light on a white surface is its irradiance over pi.
        return total * (SunStrength * MathF.PI);
    }

    /// How much of the sun's light gets through the air to a point.
    private static float3 Transmittance(float3 from, float3 sun)
    {
        if (Hits(from, sun))
            return default;

        const int steps = 12;
        var step = Exit(from, sun) / steps;
        var opticalR = 0f;
        var opticalM = 0f;
        var opticalO = 0f;
        for (var i = 0; i < steps; i++)
        {
            var height = MathEx.Length(from + sun * ((i + 0.5f) * step)) - EarthRadius;
            opticalR += MathF.Exp(-height / RayleighHeight) * step;
            opticalM += MathF.Exp(-height / MieHeight) * step;
            opticalO += OzoneDensity(height) * step;
        }

        return Extinction(opticalR, opticalM, opticalO);
    }

    private static float3 Extinction(float r, float m, float o) => new(
        MathF.Exp(-(Rayleigh.X * r + Mie * 1.1f * m + Ozone.X * o)),
        MathF.Exp(-(Rayleigh.Y * r + Mie * 1.1f * m + Ozone.Y * o)),
        MathF.Exp(-(Rayleigh.Z * r + Mie * 1.1f * m + Ozone.Z * o)));

    /// A layer about 30 km thick, around 25 km up. It's what keeps the sky blue at twilight rather than going grey.
    private static float OzoneDensity(float height) => MathF.Max(0f, 1f - MathF.Abs(height - 25000f) / 15000f);

    /// Whether a ray from a point runs into the ground.
    private static bool Hits(float3 from, float3 direction)
    {
        var b = MathEx.Dot(from, direction);
        return b < 0f && MathEx.Dot(from, from) - b * b < EarthRadius * EarthRadius;
    }

    /// How far a ray from inside the air travels before leaving it.
    private static float Exit(float3 from, float3 direction)
    {
        var b = MathEx.Dot(from, direction);
        var c = MathEx.Dot(from, from) - AirRadius * AirRadius;
        return -b + MathF.Sqrt(MathF.Max(b * b - c, 0f));
    }
}
