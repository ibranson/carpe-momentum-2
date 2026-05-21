using System.Threading.Channels;
using CarpeMomentum.Adapter.Tws;
using CarpeMomentum.Proto.V1;
using Grpc.Core;

namespace CarpeMomentum.Adapter.Services;

public class MarketDataServiceImpl : MarketDataService.MarketDataServiceBase
{
    private readonly TwsConnection _tws;
    private readonly ILogger<MarketDataServiceImpl> _logger;

    public MarketDataServiceImpl(TwsConnection tws, ILogger<MarketDataServiceImpl> logger)
    {
        _tws = tws;
        _logger = logger;
    }

    // Real-time bid/ask/last for a set of tickers, multiplexed onto a
    // single gRPC stream. Cancellation of the gRPC call cancels each
    // underlying IBKR market data subscription.
    //
    // Phase 1 implementation. Future enhancements (refcounted shared
    // subscriptions when multiple gRPC clients request the same ticker,
    // generic tick list 233 for VWAP, etc.) deferred to Phase 1.1+.
    public override async Task StreamQuotes(
        StreamQuotesRequest request,
        IServerStreamWriter<StreamQuotesResponse> responseStream,
        ServerCallContext context)
    {
        EnsureConnected();
        if (request.Tickers.Count == 0) return;

        // Unbounded so a slow gRPC reader can't backpressure IBKR's
        // callback thread (which would cascade into other subscriptions
        // sharing the same EReader). Memory grows under sustained
        // imbalance; acceptable for single-user single-machine deployment.
        var channel = Channel.CreateUnbounded<QuoteUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        foreach (var ticker in request.Tickers)
        {
            await _tws.SubscribeQuotesAsync(
                ticker,
                request.IncludeExtendedHours,
                channel.Writer,
                context.CancellationToken);
        }

        _logger.LogInformation(
            "StreamQuotes subscribed to {Count} tickers: {Tickers}",
            request.Tickers.Count, string.Join(",", request.Tickers));

        try
        {
            await foreach (var quote in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(
                    new StreamQuotesResponse { Quote = quote },
                    context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — client cancelled the stream.
        }
        finally
        {
            _logger.LogInformation(
                "StreamQuotes ended for tickers: {Tickers}",
                string.Join(",", request.Tickers));
        }
    }

    // One-shot historical bars. Used by the TradingView chart datafeed
    // for initial load and during pan/zoom into historical ranges.
    public override async Task<GetHistoricalBarsResponse> GetHistoricalBars(
        GetHistoricalBarsRequest request,
        ServerCallContext context)
    {
        EnsureConnected();
        ValidateTicker(request.Ticker);
        ValidateResolution(request.Resolution);

        if (request.From is null || request.To is null)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "from and to timestamps are required."));
        }

        var fromUtc = request.From.ToDateTime();
        var toUtc = request.To.ToDateTime();
        if (fromUtc >= toUtc)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"from ({fromUtc:o}) must be earlier than to ({toUtc:o})."));
        }

        _logger.LogInformation(
            "GetHistoricalBars {Ticker} {Resolution} {From:o} → {To:o} (extHours={ExtHours})",
            request.Ticker, request.Resolution, fromUtc, toUtc, request.IncludeExtendedHours);

        IReadOnlyList<Bar> bars;
        try
        {
            bars = await _tws.GetHistoricalBarsAsync(
                request.Ticker,
                request.Resolution,
                fromUtc,
                toUtc,
                request.IncludeExtendedHours,
                context.CancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("IBKR bars request failed"))
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }

        var response = new GetHistoricalBarsResponse();
        response.Bars.AddRange(bars);
        return response;
    }

    // Live bars stream. Sub-day resolutions go through reqRealTimeBars +
    // adapter aggregation (extended hours supported). Day1 resolution
    // goes through reqHistoricalData(keepUpToDate=true) and is RTH-bound.
    public override async Task StreamRealTimeBars(
        StreamRealTimeBarsRequest request,
        IServerStreamWriter<StreamRealTimeBarsResponse> responseStream,
        ServerCallContext context)
    {
        EnsureConnected();
        ValidateTicker(request.Ticker);
        ValidateResolution(request.Resolution);

        var channel = Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        await _tws.SubscribeRealTimeBarsAsync(
            request.Ticker,
            request.Resolution,
            request.EmitPartialBars,
            request.IncludeExtendedHours,
            channel.Writer,
            context.CancellationToken);

        _logger.LogInformation(
            "StreamRealTimeBars subscribed: {Ticker} {Resolution} emitPartial={Partial} extHours={Ext}",
            request.Ticker, request.Resolution, request.EmitPartialBars, request.IncludeExtendedHours);

        try
        {
            await foreach (var bar in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(
                    new StreamRealTimeBarsResponse { Bar = bar },
                    context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — client cancelled.
        }
        finally
        {
            _logger.LogInformation(
                "StreamRealTimeBars ended: {Ticker} {Resolution}",
                request.Ticker, request.Resolution);
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

    private static void ValidateTicker(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ticker is required."));
        }
    }

    private static void ValidateResolution(BarResolution resolution)
    {
        if (resolution == BarResolution.Unspecified)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "resolution is required."));
        }
    }
}
