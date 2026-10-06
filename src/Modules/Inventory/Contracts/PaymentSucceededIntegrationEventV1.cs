using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.Inventory.Contracts;

// Inbound contract only. Phase 8 must supply a verified, trusted producer and explicit warehouse allocation.
public sealed record PaymentSucceededIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid PaymentId, Guid OrderId,
    Guid ActorId, decimal Amount, string Currency, IReadOnlyList<PaymentStockLine> StockLines) : IIntegrationEvent
{
    public string EventType => "payments.succeeded";
    public int Version => 1;
}
public sealed record PaymentStockLine(Guid WarehouseId, Guid ProductVariantId, long Quantity);
public sealed record InventoryUnavailableIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid PaymentId, Guid OrderId,
    string Reason) : IIntegrationEvent
{
    public string EventType => "inventory.unavailable";
    public int Version => 1;
}
