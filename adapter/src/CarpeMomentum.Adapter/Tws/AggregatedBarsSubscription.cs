using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using ProtoBar = CarpeMomentum.Proto.V1.Bar;
using ProtoBarResolution = CarpeMomentum.Proto.V1.BarResolution;

namespace CarpeMomentum.Adapter.Tws;

// Streams aggregated bars derived from IBKR's 5-second reqRealTimeBars.
// Sister class to BarsSubscription, but uses the realtimeBar EWrapper
// callback instead of historicalData. Supports extended hours
// (useRTH=0) which the keepUpToDate path can't.
internal sealed class AggregatedBarsSubscription
{
    private readonly string _ticker;
    private readonly ProtoBarResolution _resolution;
    private readonly ChannelWriter<ProtoBar> _writer;
    private readonly bool _emitPartialBars;
    private readonly BarAggregator _aggregator;

    public AggregatedBarsSubscription(
        string ticker,
        ProtoBarResolution resolution,
        ChannelWriter<ProtoBar> writer,
        bool emitPartialBars)
    {
        _ticker = ticker;
        _resolution = resolution;
        _writer = writer;
        _emitPartialBars = emitPartialBars;
        _aggregator = new BarAggregator(BucketSecondsFor(resolution));
    }

    public void OnRealtimeBar(
        long unixTime,
        double open, double high, double low, double close,
        decimal volume, decimal wap)
    {
        var (completed, current) = _aggregator.AddBar(unixTime, open, high, low, close, volume, wap);

        if (completed is { } closed)
        {
            EmitBar(closed, isPartial: false);
        }
        if (_emitPartialBars)
        {
            EmitBar(current, isPartial: true);
        }
    }

    public void OnError(int errorCode, string errorMsg)
    {
        _writer.TryComplete(
            new InvalidOperationException($"IBKR realtime bars failed [{errorCode}]: {errorMsg}"));
    }

    private void EmitBar(BarAggregator.AggregatedBar bar, bool isPartial)
    {
        var protoBar = new ProtoBar
        {
            Ticker = _ticker,
            OpenTime = Timestamp.FromDateTime(
                DateTimeOffset.FromUnixTimeSeconds(bar.UnixStart).UtcDateTime),
            Resolution = _resolution,
            OpenMicros = ToMicros(bar.Open),
            HighMicros = ToMicros(bar.High),
            LowMicros = ToMicros(bar.Low),
            CloseMicros = ToMicros(bar.Close),
            Volume = bar.Volume,
            VwapMicros = ToMicros(bar.Vwap),
            IsPartial = isPartial,
        };
        _writer.TryWrite(protoBar);
    }

    private static long ToMicros(double price) =>
        (long)Math.Round(price * 1_000_000.0);

    // Map our BarResolution to seconds. Day1 is intentionally excluded —
    // TwsConnection routes Day1 to the keepUpToDate path instead.
    private static int BucketSecondsFor(ProtoBarResolution resolution) => resolution switch
    {
        ProtoBarResolution.Seconds10 => 10,
        ProtoBarResolution.Minute1 => 60,
        ProtoBarResolution.Minute5 => 300,
        ProtoBarResolution.Minute15 => 900,
        ProtoBarResolution.Minute30 => 1800,
        ProtoBarResolution.Hour1 => 3600,
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolution),
            $"Resolution {resolution} is not supported by 5-sec aggregation; route to reqHistoricalData."),
    };
}
