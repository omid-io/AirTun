namespace AirTun.Core;

public static class AirTunConfig
{
    public const string AppId = "airtun";
    public const int ProtocolVersion = 1;
    public const int DefaultSocksPort = 27510;
    public const int DefaultBeaconPort = 47880;
    public const int PinLength = 4;
    public const int BufferSize = 32768;
    public static readonly TimeSpan BeaconStaleTimeout = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
}
