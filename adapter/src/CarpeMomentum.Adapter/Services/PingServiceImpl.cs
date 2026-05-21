using CarpeMomentum.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace CarpeMomentum.Adapter.Services;

public class PingServiceImpl : PingService.PingServiceBase
{
    private readonly ILogger<PingServiceImpl> _logger;

    public PingServiceImpl(ILogger<PingServiceImpl> logger)
    {
        _logger = logger;
    }

    public override Task<GreetResponse> Greet(GreetRequest request, ServerCallContext context)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "trader" : request.Name;
        _logger.LogInformation("Greet called by {Name}", name);

        return Task.FromResult(new GreetResponse
        {
            Message = $"Welcome, {name}. The boundary holds.",
            AdapterVersion = "0.1.0-phase0",
            ServerTime = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }
}
