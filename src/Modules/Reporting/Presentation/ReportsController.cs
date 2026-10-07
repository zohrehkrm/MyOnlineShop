using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Reporting.Contracts;

namespace MyOnlineShop.Reporting.Presentation;

[ApiController, Authorize(Policy = IdentityPermissions.ViewReports), Route("api/v1/reports")]
public sealed class ReportsController(IReportingQueries queries) : ControllerBase
{
    private IActionResult Envelope<T>(T data) => Ok(new ApiResponse<T>(data, HttpContext.TraceIdentifier));
    [HttpGet("dashboard"), Authorize(Policy = IdentityPermissions.ReportSales), Authorize(Policy = IdentityPermissions.ReportInventory), Authorize(Policy = IdentityPermissions.ReportCustomers)]
    public async Task<IActionResult> Dashboard([FromQuery] ReportRequest query, CancellationToken ct) => Envelope(await queries.DashboardAsync(query, ct));
    [HttpGet("sales"), Authorize(Policy = IdentityPermissions.ReportSales)]
    public async Task<IActionResult> Sales([FromQuery] SalesRequest query, CancellationToken ct) => Envelope(await queries.SalesAsync(query, ct));
    [HttpGet("orders"), Authorize(Policy = IdentityPermissions.ReportSales)]
    public async Task<IActionResult> Orders([FromQuery] ReportRequest query, CancellationToken ct) => Envelope(await queries.OrdersAsync(query, ct));
    [HttpGet("products"), Authorize(Policy = IdentityPermissions.ReportSales)]
    public async Task<IActionResult> Products([FromQuery] ReportRequest query, CancellationToken ct) => Envelope(await queries.ProductsAsync(query, ct));
    [HttpGet("inventory"), Authorize(Policy = IdentityPermissions.ReportInventory)]
    public async Task<IActionResult> Inventory([FromQuery] InventoryRequest query, CancellationToken ct) => Envelope(await queries.InventoryAsync(query, ct));
    [HttpGet("customers"), Authorize(Policy = IdentityPermissions.ReportCustomers), Authorize(Policy = IdentityPermissions.ReportSales)]
    public async Task<IActionResult> Customers([FromQuery] ReportRequest query, CancellationToken ct) => Envelope(await queries.CustomersAsync(query, ct));
    [HttpGet("wallet"), Authorize(Policy = IdentityPermissions.ReportFinancial)]
    public async Task<IActionResult> Wallet([FromQuery] ReportRequest query, CancellationToken ct) => Envelope(await queries.WalletAsync(query, ct));
    [HttpGet("shipping"), Authorize(Policy = IdentityPermissions.ViewShipments)]
    public async Task<IActionResult> Shipping([FromQuery] ShippingRequest query, CancellationToken ct) => Envelope(await queries.ShippingAsync(query, ct));
}
