using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MyOnlineShop.Refund.Domain;
using MyOnlineShop.Refund.Infrastructure.Persistence;
using Xunit;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Tests;

public sealed class ModelTests
{
    [Fact]
    public void Offline_sql_model_has_owned_schema_financial_constraints_unique_keys_and_no_pending_changes()
    {
        using var db = new RefundDbContext(new DbContextOptionsBuilder<RefundDbContext>()
            .UseSqlServer("Server=localhost;Database=RefundOffline;Integrated Security=True").Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(RefundAggregate))!;
        Assert.Equal("refund", entity.GetSchema());
        Assert.Equal(18, entity.FindProperty("Amount")!.GetPrecision()); Assert.Equal(4, entity.FindProperty("Amount")!.GetScale());
        Assert.True(entity.FindProperty("RowVersion")!.IsConcurrencyToken);
        Assert.Contains(entity.GetIndexes(), value => value.IsUnique && value.Properties.Single().Name == "PaymentId");
        Assert.Contains(entity.GetIndexes(), value => value.IsUnique && value.Properties.Single().Name == "IdempotencyKey");
        Assert.Empty(entity.GetForeignKeys()); Assert.Equal(5, entity.GetCheckConstraints().Count());
        Assert.Single(db.Database.GetMigrations()); Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.Refunds.AsNoTracking().Where(value => value.Status == RefundStatus.Pending && value.NextAttemptAtUtc <= DateTimeOffset.UtcNow)
            .OrderBy(value => value.NextAttemptAtUtc).Take(20).Select(value => value.Id).ToQueryString();
        Assert.Contains("[refund].[Refunds]", sql);
        // Metadata/SQL generation only: no connection is opened and no migration applied.
    }
}
