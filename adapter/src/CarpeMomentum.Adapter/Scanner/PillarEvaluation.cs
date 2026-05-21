namespace CarpeMomentum.Adapter.Scanner;

// Output of the 5-Pillar evaluator. Per-pillar strengths are nullable
// because the corresponding input may be unknown. SetupQuality is the
// weighted average over KNOWN pillars only (weights renormalized over
// the known subset) — so a setup with 3 strong known pillars doesn't
// get artificially dragged down by 2 unknowns.
//
// The wire format (proto3 int32) uses -1 as the sentinel for unknown.
internal sealed record PillarEvaluation
{
    public int? PriceStrength { get; init; }
    public int? GainStrength { get; init; }
    public int? ShareFloatStrength { get; init; }
    public int? RvolStrength { get; init; }
    public int? CatalystStrength { get; init; }

    public int SetupQuality { get; init; }

    // True if all 5 pillars passed their hard threshold checks
    // (i.e. the symbol is "qualifying" rather than just present).
    public bool AllHardThresholdsPassed { get; init; }

    public static int? ToWire(int? strength) => strength;
    public static int ToWireSentinel(int? strength) => strength ?? -1;
}
