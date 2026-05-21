using CarpeMomentum.Proto.V1;

namespace CarpeMomentum.Adapter.Scanner;

// Pure 5-Pillar strength evaluator. Stateless — output depends only on
// (inputs, config). Use one instance per scan stream; pass new config
// via the constructor when the user updates settings.
//
// See memory/project_five_pillars.md for the canonical defaults and
// the rationale behind each pillar's strength curve.
//
// Strength curve shape (used for Price, Float, RVOL):
//
//     0 ─────╱─────  100  ─────╲─────  0
//      hard  soft     strong    soft   hard
//      min   min      band      max    max
//
// Inside the strong band → 100. Linearly scaled to 0 at the hard
// thresholds. Outside the hard thresholds → 0 (and symbol is excluded
// from qualifying).
internal sealed class PillarEvaluator
{
    private readonly PillarConfig _config;
    private readonly double _wPrice, _wGain, _wFloat, _wRvol, _wCatalyst;

    public PillarEvaluator(PillarConfig config)
    {
        _config = config;

        // Pre-normalize weights to a sum of 1.0 over ALL pillars (we
        // renormalize over the KNOWN subset per evaluation in
        // ComputeSetupQuality, but pre-stashing the raw values makes
        // that subset arithmetic cheap).
        var w = config.Weights ?? new PillarWeights
        {
            Price = 0.2, Gain = 0.2, ShareFloat = 0.2, Rvol = 0.2, Catalyst = 0.2,
        };
        var total = w.Price + w.Gain + w.ShareFloat + w.Rvol + w.Catalyst;
        if (total <= 0)
        {
            // Defensive — caller passed all-zero weights. Fall back to equal.
            _wPrice = _wGain = _wFloat = _wRvol = _wCatalyst = 0.2;
        }
        else
        {
            _wPrice = w.Price / total;
            _wGain = w.Gain / total;
            _wFloat = w.ShareFloat / total;
            _wRvol = w.Rvol / total;
            _wCatalyst = w.Catalyst / total;
        }
    }

    public PillarEvaluation Evaluate(PillarInputs inputs)
    {
        var price = ComputePriceStrength(inputs.LastPriceMicros);
        var gain = ComputeGainStrength(inputs.PercentGain);
        var fl = ComputeFloatStrength(inputs.ShareFloat);
        var rvol = ComputeRvolStrength(inputs.Rvol);
        var cat = ComputeCatalystStrength(inputs.Catalyst);

        var setupQuality = ComputeSetupQuality(price, gain, fl, rvol, cat);

        var allPassed =
            (price ?? 0) > 0 &&
            (gain ?? 0) > 0 &&
            (fl ?? 0) > 0 &&
            (rvol ?? 0) > 0 &&
            (cat ?? 0) > 0;

        return new PillarEvaluation
        {
            PriceStrength = price,
            GainStrength = gain,
            ShareFloatStrength = fl,
            RvolStrength = rvol,
            CatalystStrength = cat,
            SetupQuality = setupQuality,
            AllHardThresholdsPassed = allPassed,
        };
    }

    // ---- Per-pillar ----

    private int? ComputePriceStrength(long? priceMicros)
    {
        if (priceMicros is null) return null;
        var p = priceMicros.Value;

        // Hard threshold check.
        if (p < _config.PriceMinMicros || p > _config.PriceMaxMicros) return 0;

        var band = _config.StrengthBands?.Price;
        if (band is null) return 100;

        // Inside strong band → 100.
        if (p >= band.StrongMinMicros && p <= band.StrongMaxMicros) return 100;

        // Below strong band: linear from 0 (at hard min) to 100 (at strong min).
        if (p < band.StrongMinMicros)
        {
            var range = band.StrongMinMicros - _config.PriceMinMicros;
            if (range <= 0) return 100;
            var ratio = (double)(p - _config.PriceMinMicros) / range;
            return ClampToBucket(ratio * 100);
        }

        // Above strong band: linear from 100 (at strong max) to 0 (at hard max).
        var topRange = _config.PriceMaxMicros - band.StrongMaxMicros;
        if (topRange <= 0) return 100;
        var topRatio = (double)(_config.PriceMaxMicros - p) / topRange;
        return ClampToBucket(topRatio * 100);
    }

    private int? ComputeGainStrength(double? percentGain)
    {
        if (percentGain is null) return null;
        var g = percentGain.Value;

        // Hard threshold.
        if (g < _config.GainMinPercent) return 0;

        var band = _config.StrengthBands?.Gain;
        if (band is null) return 100;

        // Above strong threshold: 100 plateau (exceptional reversal risk
        // is acknowledged by the trader, not penalized in the score).
        if (g >= band.StrongMinPercent) return 100;

        // Between hard min and strong min: linear ramp.
        var range = band.StrongMinPercent - _config.GainMinPercent;
        if (range <= 0) return 100;
        var ratio = (g - _config.GainMinPercent) / range;
        return ClampToBucket(ratio * 100);
    }

    private int? ComputeFloatStrength(long? shareFloat)
    {
        if (shareFloat is null) return null;
        var f = shareFloat.Value;

        // Hard cap.
        if (f > _config.FloatMaxShares) return 0;
        if (f <= 0) return 0;

        var band = _config.StrengthBands?.ShareFloat;
        if (band is null) return 100;

        // Inside ideal band → 100.
        if (f >= band.IdealMinShares && f <= band.IdealMaxShares) return 100;

        // Below ideal (nano float — squeeze risk). Score reduces below
        // ideal_min toward a floor of ~70 at very low floats (still
        // "good" because low-float gappers are the canonical setup,
        // but acknowledges thinness risk).
        if (f < band.IdealMinShares)
        {
            if (band.IdealMinShares <= 0) return 100;
            var ratio = (double)f / band.IdealMinShares;  // 0..1
            return ClampToBucket(70 + ratio * 30);         // 70..100
        }

        // Above ideal: linear from 100 (at ideal_max) to 0 (at hard max).
        var topRange = _config.FloatMaxShares - band.IdealMaxShares;
        if (topRange <= 0) return 100;
        var topRatio = (double)(_config.FloatMaxShares - f) / topRange;
        return ClampToBucket(topRatio * 100);
    }

    private int? ComputeRvolStrength(double? rvol)
    {
        if (rvol is null) return null;
        var r = rvol.Value;

        if (r < _config.RvolMin) return 0;

        var band = _config.StrengthBands?.Rvol;
        if (band is null) return 100;

        // Explosive: cap at 100 (already very strong).
        if (r >= band.ExplosiveMin) return 100;
        if (r >= band.StrongMin) return 100;

        // Between hard min and strong min: linear ramp.
        var range = band.StrongMin - _config.RvolMin;
        if (range <= 0) return 100;
        var ratio = (r - _config.RvolMin) / range;
        return ClampToBucket(ratio * 100);
    }

    private int? ComputeCatalystStrength(PillarInputs.CatalystInfo? catalyst)
    {
        if (catalyst is null) return null;

        var freshness = _config.CatalystFreshness?.ToTimeSpan() ?? TimeSpan.FromHours(72);
        var age = DateTime.UtcNow - catalyst.CatalystUtc;
        if (age > freshness) return 0;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;  // future-dated news — treat as fresh

        // Category weight: heavyweight catalysts score higher.
        var categoryWeight = catalyst.Category switch
        {
            CatalystCategory.Fda => 100,
            CatalystCategory.Earnings => 100,
            CatalystCategory.MergerAcquisition => 95,
            CatalystCategory.Contract => 85,
            CatalystCategory.Sector => 70,
            CatalystCategory.Analyst => 60,
            _ => 50,
        };

        // Freshness decay: linear from 100% at age=0 to 30% at freshness window edge.
        var ageRatio = freshness.TotalSeconds <= 0 ? 0 : age.TotalSeconds / freshness.TotalSeconds;
        var freshnessFactor = 1.0 - 0.7 * ageRatio;  // 1.0 → 0.3

        return ClampToBucket(categoryWeight * freshnessFactor);
    }

    // ---- Aggregate ----

    private int ComputeSetupQuality(int? price, int? gain, int? fl, int? rvol, int? catalyst)
    {
        // Weighted average over KNOWN pillars only. Weights are
        // renormalized so missing inputs don't drag down the aggregate.
        double sum = 0, totalWeight = 0;

        void Add(int? value, double weight)
        {
            if (value is null) return;
            sum += value.Value * weight;
            totalWeight += weight;
        }

        Add(price, _wPrice);
        Add(gain, _wGain);
        Add(fl, _wFloat);
        Add(rvol, _wRvol);
        Add(catalyst, _wCatalyst);

        if (totalWeight <= 0) return 0;
        return (int)Math.Round(sum / totalWeight);
    }

    private static int ClampToBucket(double value) =>
        (int)Math.Round(Math.Clamp(value, 0, 100));
}
