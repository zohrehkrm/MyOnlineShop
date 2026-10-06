using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Shipping.Domain;
using MyOnlineShop.Shipping.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Shipping.Tests;

public sealed class ModelTests
{
    [Fact]
    public void Offline_sql_models_preserve_owned_schema_snapshots_precision_constraints_concurrency_and_permission_seeds()
    {
        const string connection = "Server=localhost;Database=ShippingOffline;Integrated Security=True";
        using var db = new ShippingDbContext(new DbContextOptionsBuilder<ShippingDbContext>().UseSqlServer(connection).Options);
        var model = db.GetService<IDesignTimeModel>().Model; var shipment = model.FindEntityType(typeof(Shipment))!;
        Assert.Equal("shipping", shipment.GetSchema()); Assert.Equal(18, shipment.FindProperty("ShippingCost")!.GetPrecision());
        Assert.True(shipment.FindProperty("RowVersion")!.IsConcurrencyToken);
        Assert.Contains(shipment.GetIndexes(), value => value.IsUnique && value.Properties.Single().Name == "OrderId");
        Assert.Equal(DeleteBehavior.Restrict, shipment.GetForeignKeys().Single(value => value.PrincipalEntityType.ClrType == typeof(ShippingMethod)).DeleteBehavior);
        Assert.Equal(5, shipment.GetCheckConstraints().Count()); Assert.False(db.Database.HasPendingModelChanges());
        using var order = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseSqlServer(connection).Options);
        Assert.False(order.Database.HasPendingModelChanges());
        using var identity = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(connection).Options);
        Assert.False(identity.Database.HasPendingModelChanges());
        var permission = identity.GetService<IDesignTimeModel>().Model.GetEntityTypes().Single(value => value.GetTableName() == "Permissions");
        Assert.Contains(permission.GetSeedData(), value => (string)value["Key"]! == IdentityPermissions.ManageShippingMethods);
        Assert.Contains(permission.GetSeedData(), value => (string)value["Key"]! == IdentityPermissions.ManageShipments);
        Assert.Contains(permission.GetSeedData(), value => (string)value["Key"]! == IdentityPermissions.ViewShipments);
        Assert.Contains("[shipping].[Shipments]", db.Shipments.AsNoTracking().Select(value => new { value.Id, value.Address.City }).ToQueryString());
        // EF metadata/SQL generation only: no connection opened, no migration applied.
    }
}
