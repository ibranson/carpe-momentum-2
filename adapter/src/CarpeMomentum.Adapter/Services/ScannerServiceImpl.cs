using System.Threading.Channels;
using CarpeMomentum.Adapter.Scanner;
using CarpeMomentum.Adapter.Tws;
using CarpeMomentum.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace CarpeMomentum.Adapter.Services;

public class ScannerServiceImpl : ScannerService.ScannerServiceBase
{
    private readonly TwsConnection _tws;
    private readonly ILogger<ScannerServiceImpl> _logger;

    public ScannerServiceImpl(TwsConnection tws, ILogger<ScannerServiceImpl> logger)
    {
        _tws = tws;
        _logger = logger;
    }

    // Stream of qualifying symbols derived from IBKR's TOP_PERC_GAIN
    // scanner + the 5-Pillar evaluator. v1 limitations (documented in
    // memory/project_tws_integration.md):
    //
    // • Only the Gain pillar has a real input (from the scanner's
    //   `distance` field). Price/Float/RVOL/Catalyst are all "unknown"
    //   (-1 sentinel on the wire) — future sessions will source these
    //   via per-candidate market data subs, fundamentals, and news.
    // • Each scanner refresh (~30s) re-emits QualityUpdate for every
    //   candidate in the new snapshot. There's no "removed" event yet
    //   when a symbol falls out of the scan; UI is expected to track
    //   per-ticker freshness and age out stale rows.
    // • Trend and QualityCrossover are not computed yet — they need
    //   per-symbol Setup Quality history; deferred to a follow-up.
    public override async Task StreamQualifyingSymbols(
        StreamQualifyingSymbolsRequest request,
        IServerStreamWriter<StreamQualifyingSymbolsResponse> responseStream,
        ServerCallContext context)
    {
        EnsureConnected();

        var config = request.OverrideConfig ?? DefaultPillarConfig.Create();
        var evaluator = new PillarEvaluator(config);

        var snapshotChannel = Channel.CreateUnbounded<IReadOnlyList<ScannerCandidate>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        await _tws.SubscribeScannerAsync(snapshotChannel.Writer, context.CancellationToken);

        _logger.LogInformation("StreamQualifyingSymbols started");

        try
        {
            await foreach (var snapshot in snapshotChannel.Reader.ReadAllAsync(context.CancellationToken))
            {
                _logger.LogDebug("Scanner snapshot: {Count} candidates", snapshot.Count);

                foreach (var candidate in snapshot)
                {
                    var inputs = new PillarInputs
                    {
                        Ticker = candidate.Ticker,
                        LastPriceMicros = null,    // not in v1 — needs per-candidate mkt data sub
                        PercentGain = candidate.PercentGain,
                        ShareFloat = null,         // not in v1 — needs fundamentals
                        Rvol = null,               // not in v1 — needs historical avg vol
                        Catalyst = null,           // not in v1 — needs news integration
                    };

                    var eval = evaluator.Evaluate(inputs);

                    var update = new QualityUpdate
                    {
                        Ticker = candidate.Ticker,
                        Ts = Timestamp.FromDateTime(DateTime.UtcNow),

                        // Input echo (0 = unknown by convention for these fields).
                        LastPriceMicros = 0,
                        PercentGain = candidate.PercentGain ?? 0,
                        ShareFloat = 0,
                        Rvol = 0,
                        // CatalystSummary / CatalystTs / CatalystCategory left at proto defaults.

                        // Per-pillar strengths (-1 = unknown sentinel).
                        PriceStrength = eval.PriceStrength ?? -1,
                        GainStrength = eval.GainStrength ?? -1,
                        ShareFloatStrength = eval.ShareFloatStrength ?? -1,
                        RvolStrength = eval.RvolStrength ?? -1,
                        CatalystStrength = eval.CatalystStrength ?? -1,

                        SetupQuality = eval.SetupQuality,
                        Trend = Trend.Unspecified,    // requires history — deferred
                        QualityCrossover = false,      // requires history — deferred
                    };

                    await responseStream.WriteAsync(
                        new StreamQualifyingSymbolsResponse { Update = update },
                        context.CancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — client cancelled.
        }
        finally
        {
            _logger.LogInformation("StreamQualifyingSymbols ended");
        }
    }

    private void EnsureConnected()
    {
        if (!_tws.IsConnected)
        {
            throw new RpcException(new Status(
                StatusCode.Unavailable,
                "TWS adapter is not connected. Check TWS is running and the adapter's connection settings."));
        }
    }
}
