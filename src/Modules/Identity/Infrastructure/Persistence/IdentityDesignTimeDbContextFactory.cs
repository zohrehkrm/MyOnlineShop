using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyOnlineShop.Identity.Infrastructure.Persistence;

// EF tooling creates only the context; it must never run application startup initialization.
public sealed class IdentityDesignTimeDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Set ConnectionStrings__SqlServer for Identity design-time tooling. Migration generation does not connect to SQL Server.");
        return new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer(connection, sql =>
            {
                sql.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "identity");
            }).Options);
    }
}
