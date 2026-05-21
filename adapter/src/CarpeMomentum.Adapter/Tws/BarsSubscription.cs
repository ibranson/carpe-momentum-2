using System.Threading.Channels;
using ProtoBar = CarpeMomentum.Proto.V1.Bar;
using ProtoBarResolution = CarpeMomentum.Proto.V1.BarResolution;

namespace CarpeMomentum.Adapter.Tws;

// Represents one in-flight reqHistoricalData request — either:
//   • One-shot historical fetch (GetHistoricalBars): bars accumulate in a
//     List, then completed via a TaskCompletionSource on historicalDataEnd.
//   • Live streaming (StreamRealTimeBars with keepUpToDate=true): bars
//     written to a Channel as they arrive, never completes.
//
// Same dispatch path because IBApi uses the same reqId and callbacks
// (historicalData / historicalDataUpdate / historicalDataEnd) for both.
internal sealed class BarsSubscription
{
    private readonly string _ticker;
    private readonly ProtoBarResolution _resolution;

    // One-shot mode state (null in streaming mode).
    private readonly List<ProtoBar>? _accumulator;
    private readonly TaskCompletionSource<IReadOnlyList<ProtoBar>>? _tcs;

    // Streaming mode state (null in one-shot mode).
    private readonly ChannelWriter<ProtoBar>? _writer;
    private readonly bool _emitPartialBars;

    private BarsSubscription(
        string ticker,
        ProtoBarResolution resolution,
        List<ProtoBar>? accumulator,
        TaskCompletionSource<IReadOnlyList<ProtoBar>>? tcs,
        ChannelWriter<ProtoBar>? writer,
        bool emitPartialBars)
    {
        _ticker = ticker;
        _resolution = resolution;
        _accumulator = accumulator;
        _tcs = tcs;
        _writer = writer;
        _emitPartialBars = emitPartialBars;
    }

    public bool IsOneShot => _accumulator is not null;
    public Task<IReadOnlyList<ProtoBar>>? CompletionTask => _tcs?.Task;

    public static BarsSubscription ForHistoricalOneShot(
        string ticker, ProtoBarResolution resolution) =>
        new(
            ticker, resolution,
            accumulator: new List<ProtoBar>(),
            tcs: new TaskCompletionSource<IReadOnlyList<ProtoBar>>(TaskCreationOptions.RunContinuationsAsynchronously),
            writer: null,
            emitPartialBars: false);

    public static BarsSubscription ForLiveStream(
        string ticker,
        ProtoBarResolution resolution,
        ChannelWriter<ProtoBar> writer,
        bool emitPartialBars) =>
        new(
            ticker, resolution,
            accumulator: null,
            tcs: null,
            writer: writer,
            emitPartialBars: emitPartialBars);

    // historicalData callback: a completed bar from IBKR.
    public void OnBar(IBApi.Bar bar)
    {
        var protoBar = BarConversions.ToProtoBar(_ticker, _resolution, bar, isPartial: false);
        if (_accumulator is not null)
        {
            _accumulator.Add(protoBar);
        }
        else
        {
            _writer?.TryWrite(protoBar);
        }
    }

    // historicalDataUpdate callback: an update to the current (in-progress)
    // bar during keepUpToDate=true mode. Only emit if the caller opted in.
    public void OnUpdate(IBApi.Bar bar)
    {
        if (_writer is null) return;
        if (!_emitPartialBars) return;

        var protoBar = BarConversions.ToProtoBar(_ticker, _resolution, bar, isPartial: true);
        _writer.TryWrite(protoBar);
    }

    // historicalDataEnd callback: marks the end of the historical replay.
    // For one-shot requests, this completes the awaiter.
    // For streaming requests, the IBApi continues to fire historicalDataUpdate
    // afterward, so we do nothing here.
    public void OnEnd()
    {
        _tcs?.TrySetResult((IReadOnlyList<ProtoBar>)_accumulator! ?? Array.Empty<ProtoBar>());
    }

    public void OnError(int errorCode, string errorMsg)
    {
        var ex = new InvalidOperationException(
            $"IBKR bars request failed [{errorCode}]: {errorMsg}");
        _tcs?.TrySetException(ex);
        _writer?.TryComplete(ex);
    }
}
