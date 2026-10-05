using Microsoft.EntityFrameworkCore;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Foundation.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FOUNDATION_TEST_SQL_SERVER")))
            Skip = "Set FOUNDATION_TEST_SQL_SERVER to run the read-only SQL Server connectivity test.";
    }
}

public sealed class SqlServerConnectivityTests
{
    [SqlServerFact]
    public async Task Sql_server_connection_executes_read_only_probe()
    {
        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("FOUNDATION_TEST_SQL_SERVER"))
            .Options;
        await using var context = new FoundationDbContext(options);
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }
}
