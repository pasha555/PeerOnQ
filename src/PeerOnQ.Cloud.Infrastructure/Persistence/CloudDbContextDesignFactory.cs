using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PeerOnQ.Cloud.Infrastructure.Persistence;

public sealed class CloudDbContextDesignFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_MIGRATIONS_POSTGRES")
            ?? "Host=localhost;Database=peeronq;Username=peeronq";
        var options = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new CloudDbContext(options);
    }
}
