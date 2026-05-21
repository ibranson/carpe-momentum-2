namespace CarpeMomentum.Adapter.Scanner;

// One match returned by IBKR's reqScannerSubscription. The interesting
// data depends on the scan code — IBKR overloads the `distance` field
// to carry the primary metric. For TOP_PERC_GAIN (our default scan), it
// holds the percent gain.
//
// We parse `distance` into typed fields here so downstream code doesn't
// have to know which scan code is in use.
public sealed record ScannerCandidate
{
    public required int Rank { get; init; }
    public required string Ticker { get; init; }
    public string Exchange { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string Industry { get; init; } = "";

    // From the scan's `distance` field. Interpretation varies by scan
    // code — for TOP_PERC_GAIN it's the % gain (0.47 = 47%).
    public double? PercentGain { get; init; }
}
