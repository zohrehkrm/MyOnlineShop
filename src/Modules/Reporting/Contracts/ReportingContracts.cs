namespace MyOnlineShop.Reporting.Contracts;

public class ReportRequest
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public string? Currency { get; init; }
    public string? OrderStatus { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
public sealed class SalesRequest : ReportRequest { public string Bucket { get; init; } = "Day"; }
public sealed class InventoryRequest : ReportRequest
{
    public Guid? WarehouseId { get; init; }
    public bool LowStockOnly { get; init; }
}
public sealed class ShippingRequest : ReportRequest { public string? ShipmentStatus { get; init; } }
public sealed record ReportWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Currency, string? OrderStatus);
public sealed record ReportPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
public sealed record StatusCount(string Status, long Count);
public sealed record SalesCurrencyDto(string Currency, long OrderCount, decimal Subtotal, decimal Discounts, decimal Shipping,
    decimal RecordedPaidOrderValue, decimal AverageOrderValue);
public sealed record SalesPeriodDto(string Currency, int Year, int Month, int Day, long OrderCount, decimal RecordedPaidOrderValue);
public sealed record SalesReportDto(ReportWindow Window, string Basis, IReadOnlyList<SalesCurrencyDto> Totals, ReportPage<SalesPeriodDto> Periods);
public sealed record OrderReportRow(Guid OrderId, string Status, string Currency, decimal PayableAmount, DateTimeOffset CreatedAtUtc);
public sealed record OrderReportDto(ReportWindow Window, IReadOnlyList<StatusCount> Statuses, ReportPage<OrderReportRow> Orders);
public sealed record VariantSalesDto(Guid ProductVariantId, string Currency, long Quantity, decimal NetItemSales, long OrderFrequency);
public sealed record BuyerSalesDto(Guid UserId, string Currency, long PaidOrderCount, decimal RecordedPaidOrderValue);
public sealed record CustomerCounts(long RegisteredCustomers, long ActiveRegisteredCustomers);
public sealed record CustomerReportDto(ReportWindow Window, CustomerCounts Registrations, ReportPage<BuyerSalesDto> Buyers);
public sealed record CatalogCounts(long CurrentProducts, long CurrentActiveProducts);
public sealed record StockReportRow(Guid WarehouseId, Guid ProductVariantId, long Quantity, long LowStockThreshold, bool IsActive, bool WarehouseActive);
public sealed record MovementSummaryDto(Guid WarehouseId, string Type, long MovementCount, long QuantityAdded, long QuantityDeducted, long NetQuantity);
public sealed record InventoryReportDto(ReportWindow MovementWindow, long CurrentLowStockVariants, ReportPage<StockReportRow> CurrentStock,
    ReportPage<MovementSummaryDto> Movements);
public sealed record WalletSummaryDto(string Currency, long TransactionCount, decimal Credits, decimal Debits, decimal InventoryRefundCredits);
public sealed record ShippingStatusDto(string Status, long Count);
public sealed record ShippingMethodSummaryDto(Guid ShippingMethodId, long Count);
public sealed record ShippingReportDto(ReportWindow CreationWindow, IReadOnlyList<ShippingStatusDto> Statuses,
    ReportPage<ShippingMethodSummaryDto> Methods);
public sealed record DashboardDto(ReportWindow OrderWindow, IReadOnlyList<StatusCount> OrderStatuses, IReadOnlyList<SalesCurrencyDto> RecordedSales,
    CustomerCounts CustomerRegistrations, CatalogCounts Catalog, long CurrentLowStockVariants, string SalesBasis, bool PaymentReportsAvailable);

// Explicit read ports: implementations stay inside the owning module and return only bounded DTOs/aggregates.
public interface IOrderReportSource
{
    Task<IReadOnlyList<StatusCount>> StatusesAsync(ReportWindow window, CancellationToken ct);
    Task<IReadOnlyList<SalesCurrencyDto>> SalesAsync(ReportWindow window, CancellationToken ct);
    Task<ReportPage<SalesPeriodDto>> PeriodsAsync(ReportWindow window, string bucket, int page, int size, CancellationToken ct);
    Task<ReportPage<OrderReportRow>> OrdersAsync(ReportWindow window, int page, int size, CancellationToken ct);
    Task<ReportPage<VariantSalesDto>> VariantsAsync(ReportWindow window, int page, int size, CancellationToken ct);
    Task<ReportPage<BuyerSalesDto>> BuyersAsync(ReportWindow window, int page, int size, CancellationToken ct);
}
public interface IInventoryReportSource
{
    Task<long> LowStockVariantsAsync(Guid? warehouse, CancellationToken ct);
    Task<ReportPage<StockReportRow>> StockAsync(Guid? warehouse, bool lowOnly, int page, int size, CancellationToken ct);
    Task<ReportPage<MovementSummaryDto>> MovementsAsync(ReportWindow window, Guid? warehouse, int page, int size, CancellationToken ct);
}
public interface ICustomerReportSource { Task<CustomerCounts> CountsAsync(ReportWindow window, CancellationToken ct); }
public interface ICatalogReportSource { Task<CatalogCounts> CountsAsync(CancellationToken ct); }
public interface IWalletReportSource { Task<IReadOnlyList<WalletSummaryDto>> SummaryAsync(ReportWindow window, CancellationToken ct); }
public interface IShippingReportSource { Task<ShippingReportDto> SummaryAsync(ReportWindow window, string? status, int page, int size, CancellationToken ct); }
public interface IReportingQueries
{
    Task<DashboardDto> DashboardAsync(ReportRequest query, CancellationToken ct);
    Task<SalesReportDto> SalesAsync(SalesRequest query, CancellationToken ct);
    Task<OrderReportDto> OrdersAsync(ReportRequest query, CancellationToken ct);
    Task<ReportPage<VariantSalesDto>> ProductsAsync(ReportRequest query, CancellationToken ct);
    Task<InventoryReportDto> InventoryAsync(InventoryRequest query, CancellationToken ct);
    Task<CustomerReportDto> CustomersAsync(ReportRequest query, CancellationToken ct);
    Task<IReadOnlyList<WalletSummaryDto>> WalletAsync(ReportRequest query, CancellationToken ct);
    Task<ShippingReportDto> ShippingAsync(ShippingRequest query, CancellationToken ct);
}
