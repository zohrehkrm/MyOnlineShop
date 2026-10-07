using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using Xunit;

namespace MyOnlineShop.Order.Tests;

public sealed class AdministrationTests
{
    [Fact]
    public async Task Administrator_cannot_mark_paid_or_skip_transitions_and_cancellation_preserves_money()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(); var order = await h.Checkout();
        var commands = h.Services.GetRequiredService<IOrderCommands>(); var actor = Guid.NewGuid();
        await Assert.ThrowsAsync<OrderException>(() => commands.ChangeStatusAsync(actor, order.Id, "Paid", default));
        await Assert.ThrowsAsync<OrderException>(() => commands.ChangeStatusAsync(actor, order.Id, "Completed", default));
        var cancelled = await commands.ChangeStatusAsync(actor, order.Id, "Cancelled", default);
        Assert.Equal("Cancelled", cancelled.Status); Assert.Equal(order.PayableAmount, cancelled.PayableAmount);
        Assert.Equal(order.Items, cancelled.Items);
    }
    [Fact]
    public async Task Management_list_filters_status_and_half_open_dates_without_changing_snapshots()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(); var order = await h.Checkout();
        var queries = h.Services.GetRequiredService<IOrderQueries>();
        var page = await queries.ListAsync(new OrderListQuery { Status = order.Status, FromUtc = FixedClock.Now, ToUtc = FixedClock.Now.AddSeconds(1), PageSize = 1 }, default);
        Assert.Equal(order.Id, Assert.Single(page.Items).Id); Assert.Equal(1, page.TotalCount);
        Assert.Equal(order.PayableAmount, page.Items[0].PayableAmount);
        Assert.Empty((await queries.ListAsync(new OrderListQuery { ToUtc = FixedClock.Now }, default)).Items);
        Assert.Empty((await queries.ListAsync(new OrderListQuery { Status = "Cancelled" }, default)).Items);
        Assert.Empty((await queries.ListAsync(new OrderListQuery { Page = 2, PageSize = 1 }, default)).Items);
        await Assert.ThrowsAsync<OrderException>(() => queries.ListAsync(new OrderListQuery { Status = "2" }, default));
        await Assert.ThrowsAsync<OrderException>(() => queries.ListAsync(new OrderListQuery { FromUtc = FixedClock.Now, ToUtc = FixedClock.Now }, default));
        await Assert.ThrowsAsync<OrderException>(() => queries.ListAsync(new OrderListQuery { Page = int.MaxValue }, default));
    }

    [Fact]
    public async Task Management_route_requires_permission_and_validates_pagination_and_status()
    {
        using var factory = new OrderApiFactory(); using var anonymous = factory.Client();
        using var customer = factory.Client(Guid.NewGuid()); using var reader = factory.Client(Guid.NewGuid(), IdentityPermissions.ViewOrders);
        const string route = "/api/v1/orders/management";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(route)).StatusCode);
        Assert.Equal(0, (await reader.GetFromJsonAsync<ApiResponse<OrderPage>>(route + "?pageSize=1&status=Cancelled"))!.Data.TotalCount);
        foreach (var query in new[] { "page=0", "pageSize=101", "status=2", "status=Unknown", "fromUtc=2026-10-07&toUtc=2026-10-06" })
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(route + "?" + query)).StatusCode);
    }
}
