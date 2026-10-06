using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.Order.Contracts;

public sealed record OrderCreatedIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid OrderId, decimal Amount, string Currency) : IIntegrationEvent
{
    public string EventType => "orders.created";
    public int Version => 1;
}
