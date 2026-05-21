using CarpeMomentum.Proto.V1;
using Google.Protobuf.WellKnownTypes;

namespace CarpeMomentum.Adapter.Scanner;

// Canonical Ross Cameron 5-Pillar defaults. Used by ScannerService
// when no PillarConfig is supplied via SettingsService (Phase 1.x: the
// SettingsService stub returns these directly). When the user edits
// thresholds in the Settings window, the persisted config replaces these.
//
// See memory/project_five_pillars.md for rationale per pillar.
internal static class DefaultPillarConfig
{
    public static PillarConfig Create() => new()
    {
        // $1.00 ≤ price ≤ $20.00
        PriceMinMicros = 1_000_000,
        PriceMaxMicros = 20_000_000,

        // ≥ 10% intraday gain
        GainMinPercent = 0.10,

        // ≤ 20M shares outstanding (free-trading float)
        FloatMaxShares = 20_000_000,

        // ≥ 5× average daily volume
        RvolMin = 5.0,

        // 72 hours of catalyst freshness
        CatalystFreshness = Duration.FromTimeSpan(TimeSpan.FromHours(72)),

        StrengthBands = new PillarStrengthBands
        {
            // Price strong band: $2 - $10
            Price = new PriceBand
            {
                StrongMinMicros = 2_000_000,
                StrongMaxMicros = 10_000_000,
            },
            // Gain strong threshold: 20% (exceptional at 50%, just a plateau marker)
            Gain = new GainBand
            {
                StrongMinPercent = 0.20,
                ExceptionalMinPercent = 0.50,
            },
            // Float ideal band: 2M - 10M shares
            ShareFloat = new FloatBand
            {
                IdealMinShares = 2_000_000,
                IdealMaxShares = 10_000_000,
            },
            // RVOL strong: 10x. Explosive: 25x.
            Rvol = new RvolBand
            {
                StrongMin = 10.0,
                ExplosiveMin = 25.0,
            },
        },

        Weights = new PillarWeights
        {
            // Equal weights — refine via Settings → 5-Pillar Weights later.
            Price = 0.2,
            Gain = 0.2,
            ShareFloat = 0.2,
            Rvol = 0.2,
            Catalyst = 0.2,
        },
    };
}
