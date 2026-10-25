using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Redis;

public sealed class DeviceTokenValidationRedisConnection : IDisposable, IAsyncDisposable
{
    private int _disposed;

    private DeviceTokenValidationRedisConnection(IConnectionMultiplexer connection)
    {
        Connection = connection;
    }

    public IConnectionMultiplexer Connection { get; }

    public static DeviceTokenValidationRedisConnection Connect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        options.ReconnectRetryPolicy = new ExponentialRetry(1000, 10000);
        return new DeviceTokenValidationRedisConnection(ConnectionMultiplexer.Connect(options));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            Connection.Close(allowCommandsToComplete: true);
        }
        finally
        {
            Connection.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Connection.CloseAsync(allowCommandsToComplete: true).ConfigureAwait(false);
        }
        finally
        {
            Connection.Dispose();
        }
    }
}
