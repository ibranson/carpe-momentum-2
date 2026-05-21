using Microsoft.Extensions.Options;

namespace CarpeMomentum.Adapter.Tws;

// Owns the TWS connection lifecycle for the lifetime of the process.
// Runs the connect attempt in the background so that the gRPC server
// can start serving immediately — clients calling RPCs before TWS is
// reachable will receive Status.Unavailable until the connection lands.
//
// Auto-reconnect loop: if the connection drops or initial connect fails,
// retry every ReconnectDelaySeconds until cancelled.
public sealed class TwsHostedService : BackgroundService
{
    private readonly TwsConnection _tws;
    private readonly TwsConnectionOptions _options;
    private readonly ILogger<TwsHostedService> _logger;

    public TwsHostedService(
        TwsConnection tws,
        IOptions<TwsConnectionOptions> options,
        ILogger<TwsHostedService> logger)
    {
        _tws = tws;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _tws.ConnectAsync(stoppingToken);
                // Connected. Wait until disconnect.
                while (_tws.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TWS connect attempt failed");
            }

            if (!_options.AutoReconnect)
            {
                _logger.LogInformation("AutoReconnect disabled; not retrying.");
                break;
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Retrying TWS connect in {Seconds}s",
                    _options.ReconnectDelaySeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _tws.DisconnectAsync();
        await base.StopAsync(cancellationToken);
    }
}
