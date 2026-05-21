namespace CarpeMomentum.Adapter.Tws;

// Connection settings for IBKR TWS / IB Gateway. Defaults target the
// PAPER TWS instance on localhost (port 7497). Override via appsettings:
//
//   "Tws": {
//     "Host": "127.0.0.1",
//     "Port": 7497,       // 7496 = live TWS, 7497 = paper TWS,
//                         // 4001 = live Gateway, 4002 = paper Gateway
//     "ClientId": 1
//   }
//
// ClientId must be unique across simultaneous API connections to the
// same TWS instance. Use 0 for the master client (can see all orders);
// any other positive int for additional clients.
public sealed class TwsConnectionOptions
{
    public const string SectionName = "Tws";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;
    public int ClientId { get; set; } = 1;

    public bool AutoReconnect { get; set; } = true;
    public int ReconnectDelaySeconds { get; set; } = 5;

    // How long to wait for nextValidId after eConnect before giving up.
    public int ConnectTimeoutSeconds { get; set; } = 10;
}
