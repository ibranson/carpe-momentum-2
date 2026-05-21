using System.Globalization;
using System.Threading.Channels;
using CarpeMomentum.Adapter.Scanner;
using IBApi;

namespace CarpeMomentum.Adapter.Tws;

// Accumulates IBKR scannerData callbacks for one in-flight scanner
// subscription. IBKR's reqScannerSubscription delivers each match via
// a separate scannerData callback (with `rank`), then signals end of
// the snapshot via scannerDataEnd. We collect all matches in a snapshot
// list and flush to the writer on End. IBKR re-runs the scan periodically
// (default ~30s) and the cycle repeats.
internal sealed class ScannerSnapshotSubscription
{
    private readonly ChannelWriter<IReadOnlyList<ScannerCandidate>> _writer;
    private List<ScannerCandidate> _accumulator = new();

    public ScannerSnapshotSubscription(ChannelWriter<IReadOnlyList<ScannerCandidate>> writer)
    {
        _writer = writer;
    }

    public void OnCandidate(int rank, ContractDetails details, string distance)
    {
        // distance carries the primary metric for this scan code; for
        // TOP_PERC_GAIN it's a percent like "47.3" (no % sign, base-100).
        double? percentGain = null;
        if (double.TryParse(distance, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            percentGain = pct / 100.0;  // normalize "47.3" → 0.473
        }

        _accumulator.Add(new ScannerCandidate
        {
            Rank = rank,
            Ticker = details.Contract.Symbol ?? "",
            Exchange = details.Contract.PrimaryExch ?? details.Contract.Exchange ?? "",
            CompanyName = details.LongName ?? "",
            Industry = details.Industry ?? details.Category ?? "",
            PercentGain = percentGain,
        });
    }

    public void OnEnd()
    {
        // Snapshot the accumulator and start a fresh one for the next refresh.
        var snapshot = _accumulator;
        _accumulator = new List<ScannerCandidate>();
        // Order by rank (IBKR usually delivers them in order, but defensive).
        snapshot.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        _writer.TryWrite(snapshot);
    }

    public void OnError(int errorCode, string errorMsg)
    {
        _writer.TryComplete(
            new InvalidOperationException($"IBKR scanner failed [{errorCode}]: {errorMsg}"));
    }
}
