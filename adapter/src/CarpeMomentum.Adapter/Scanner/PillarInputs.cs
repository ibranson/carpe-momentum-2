using CarpeMomentum.Proto.V1;

namespace CarpeMomentum.Adapter.Scanner;

// Inputs to the 5-Pillar evaluator for a single symbol.
//
// All fields are nullable because the adapter's data sourcing is layered
// — we may have a live price quote but not yet have float data, or have
// float but no news. The evaluator handles each pillar independently
// and reports per-pillar strength as null when its input is unknown.
internal sealed record PillarInputs
{
    public required string Ticker { get; init; }

    public long? LastPriceMicros { get; init; }
    public double? PercentGain { get; init; }
    public long? ShareFloat { get; init; }
    public double? Rvol { get; init; }
    public CatalystInfo? Catalyst { get; init; }

    public sealed record CatalystInfo(
        CatalystCategory Category,
        DateTime CatalystUtc,
        string Summary);
}
