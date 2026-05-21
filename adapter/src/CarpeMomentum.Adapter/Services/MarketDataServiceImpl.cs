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
        if (!_tws.IsConnected)
        {
            throw new RpcException(new Status(
                StatusCode.Unavailable,
                "TWS adapter is not connected. Check TWS is running and the adapter's connection settings."));
        }

        if (request.Tickers.Count == 0)
        {
            return;
        }

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
}
