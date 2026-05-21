using CarpeMomentum.Adapter.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.WebHost.ConfigureKestrel(opts =>
    {
        // Single endpoint serving both HTTP/1.1 (gRPC-Web from browser/Tauri)
        // and HTTP/2 (native gRPC). Cleartext for local dev.
        opts.ListenLocalhost(5000, listenOpts =>
        {
            listenOpts.Protocols = HttpProtocols.Http1AndHttp2;
        });
    });

    builder.Services.AddGrpc();

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(
                    "http://localhost:5173",   // Vite dev server
                    "http://tauri.localhost",  // Tauri 2 Windows webview origin
                    "https://tauri.localhost") // Tauri 2 default
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders(
                    "grpc-status",
                    "grpc-message",
                    "grpc-encoding",
                    "grpc-accept-encoding");
        });
    });

    var app = builder.Build();

    app.UseRouting();
    app.UseCors();
    app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

    app.MapGrpcService<PingServiceImpl>();
    app.MapGrpcService<MarketDataServiceImpl>();
    app.MapGrpcService<ScannerServiceImpl>();
    app.MapGrpcService<OrderServiceImpl>();
    app.MapGrpcService<NewsServiceImpl>();
    app.MapGrpcService<SettingsServiceImpl>();

    app.MapGet("/", () =>
        "Carpe Momentum 2 Adapter — gRPC services at /carpe_momentum.v1.*\n" +
        "Implemented: PingService.Greet (Phase 0 smoke test)\n" +
        "Skeleton (returns Unimplemented): MarketDataService, ScannerService,\n" +
        "  OrderService, NewsService, SettingsService — see SPEC §5 Phase 1.");

    Log.Information("Adapter listening on http://localhost:5000");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Adapter terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
