using StackExchange.Redis;

namespace PeerOnQ.Admin.Api;

public sealed class AdminDataProtectionConnection(IConnectionMultiplexer connection) : IDisposable
{
    public IConnectionMultiplexer Connection { get; } = connection;
    public void Dispose() => Connection.Dispose();
}
