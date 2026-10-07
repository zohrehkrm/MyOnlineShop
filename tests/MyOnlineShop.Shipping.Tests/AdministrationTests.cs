using System.Net;
using System.Net.Http.Json;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using Xunit;

namespace MyOnlineShop.Shipping.Tests;

public sealed class AdministrationTests
{
    [Fact]
    public async Task Shipment_list_filters_and_pages_persisted_state_without_exposing_address()
    {
        using var h = new Harness(); var shipment = await h.Shipment();
        var page = await h.Queries.ListAsync(new ShipmentListQuery { Status = "Pending", OrderId = shipment.OrderId,
            ShippingMethodId = shipment.ShippingMethodId, FromUtc = shipment.CreatedAtUtc, ToUtc = shipment.CreatedAtUtc.AddSeconds(1), PageSize = 1 }, default);
        Assert.Equal(shipment.Id, Assert.Single(page.Items).Id); Assert.Equal(1, page.TotalCount);
        Assert.Empty((await h.Queries.ListAsync(new ShipmentListQuery { ToUtc = shipment.CreatedAtUtc }, default)).Items);
        Assert.Empty((await h.Queries.ListAsync(new ShipmentListQuery { Page = 2, PageSize = 1 }, default)).Items);
        await h.Status(shipment, "Preparing");
        Assert.Empty((await h.Queries.ListAsync(new ShipmentListQuery { Status = "Pending" }, default)).Items);
        Assert.Single((await h.Queries.ListAsync(new ShipmentListQuery { Status = "Preparing" }, default)).Items);
        await Assert.ThrowsAsync<ShippingException>(() => h.Queries.ListAsync(new ShipmentListQuery { Status = "1" }, default));
        await Assert.ThrowsAsync<ShippingException>(() => h.Queries.ListAsync(new ShipmentListQuery { OrderId = Guid.Empty }, default));
        await Assert.ThrowsAsync<ShippingException>(() => h.Queries.ListAsync(new ShipmentListQuery { Page = int.MaxValue }, default));
        Assert.DoesNotContain(typeof(ShipmentSummaryDto).GetProperties(), p => p.Name is "Address" or "ShippingCost" or "UserId");
    }

    [Fact]
    public async Task Shipment_list_requires_view_permission_even_for_administrator_role()
    {
        using var factory = new ShippingApiFactory(); using var anonymous = factory.Client();
        using var roleOnly = factory.Client(Guid.NewGuid()); using var manager = factory.Client(Guid.NewGuid(), IdentityPermissions.ManageShipments);
        using var reader = factory.Client(Guid.NewGuid(), IdentityPermissions.ViewShipments);
        const string route = "/api/v1/shipping/management/shipments";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await roleOnly.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(route)).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<ApiResponse<ShipmentPage>>(route))!.Data.Items);
        foreach (var query in new[] { "page=0", "pageSize=101", "status=1", "orderId=00000000-0000-0000-0000-000000000000", "fromUtc=2026-10-07&toUtc=2026-10-06" })
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(route + "?" + query)).StatusCode);
    }
}
