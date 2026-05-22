using System.Threading.Channels;
using CarpeMomentum.Adapter.Scanner;
using CarpeMomentum.Adapter.Tws;
using CarpeMomentum.Proto.V1;
using Grpc.Core;

namespace CarpeMomentum.Adapter.Services;

public class ScannerServiceImpl : ScannerService.ScannerServiceBase
{
    private readonly TwsConnection _tws;
    private readonly ILogger<ScannerServiceImpl> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ScannerServiceImpl(
        TwsConnection tws,
        ILogger<ScannerServiceImpl> logger,
        ILoggerFactory loggerFactory)
    {
        _tws = tws;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    // Stream of qualifying symbols backed by:
    //   • IBKR TOP_PERC_GAIN scanner (the candidate funnel)
    //   • Per-candidate market data subscriptions (live LastPrice, volume)
    //   • One-shot 30-day daily bars per candidate (avg vol → RVOL, prev close)
    //   • 5-Pillar evaluator → QualityUpdate emissions
    //
    // Emission is debounced per-symbol to ~500ms so the UI gets smooth
    // updates without per-tick spam.
    //
    // v1 limitations (see memory/project_tws_integration.md "Scanner pattern"):
    //   • Float pillar still unknown (needs fundamentals)
    //   • Catalyst pillar still unknown (needs news)
    //   • Trend + QualityCrossover require per-symbol Q history — deferred
    public override async Task StreamQualifyingSymbols(
        StreamQualifyingSymbolsRequest request,
        IServerStreamWriter<StreamQualifyingSymbolsResponse> responseStream,
        ServerCallContext context)
    {
        EnsureConnected();

        var config = request.OverrideConfig ?? DefaultPillarConfig.Create();
        var evaluator = new PillarEvaluator(config);

        // Output channel — the coordinator writes QualityUpdates here,
        // and we drain it onto the gRPC stream below.
        var output = Channel.CreateUnbounded<QualityUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var coordinator = new ScannerEnrichmentCoordinator(
            _tws,
            evaluator,
            output.Writer,
            _loggerFactory.CreateLogger<ScannerEnrichmentCoordinator>());

        // Run the coordinator in the background; it completes the
        // output channel when done.
        var coordinatorTask = Task.Run(
            () => coordinator.RunAsync(context.CancellationToken),
            context.CancellationToken);

        _logger.LogInformation("StreamQualifyingSymbols started");

        try
        {
            await foreach (var update in output.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(
                    new StreamQualifyingSymbolsResponse { Update = update },
                    context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — client cancelled.
        }
        finally
        {
            try { await coordinatorTask; }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Coordinator task threw"); }
            await coordinator.DisposeAsync();
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
