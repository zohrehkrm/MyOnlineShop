using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Inventory.Application;

public sealed class PaymentSucceededConsumer(IInventoryDeduction deduction, TimeProvider clock) : IMessageConsumer
{
    public string ConsumerName => "inventory.payment-succeeded.v1";
    public string EventType => "payments.succeeded";
    public int Version => 1;
    public IReadOnlyList<string> TransactionParticipants => ["inventory"];
    public static Guid OperationId(Guid paymentId, PaymentStockLine line) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"payment-stock:{paymentId:N}:{line.WarehouseId:N}:{line.ProductVariantId:N}")).AsSpan(0, 16));
    public async Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate();
        var payment = envelope.Payload.Deserialize<PaymentSucceededIntegrationEventV1>() ?? throw new InvalidMessageException();
        if (envelope.EventType != EventType || envelope.Version != Version || payment.EventId != envelope.MessageId || payment.OccurredAtUtc != envelope.OccurredAtUtc ||
            payment.PaymentId == Guid.Empty || payment.OrderId == Guid.Empty || payment.ActorId == Guid.Empty || payment.StockLines is null || payment.StockLines.Count > 100 ||
            payment.StockLines.Any(line => line is null || line.WarehouseId == Guid.Empty || line.ProductVariantId == Guid.Empty || line.Quantity is < 1 or > 999) ||
            payment.StockLines.Select(line => (line.WarehouseId, line.ProductVariantId)).Distinct().Count() != payment.StockLines.Count)
            throw new InvalidMessageException();
        try { MoneyRules.Amount(payment.Amount, payment.Currency, zeroAllowed: true); }
        catch (MoneyRuleException) { throw new InvalidMessageException(); }
        foreach (var line in payment.StockLines.OrderBy(value => value.WarehouseId).ThenBy(value => value.ProductVariantId))
        {
            try
            {
                await deduction.DeductAsync(new() { OperationId = OperationId(payment.PaymentId, line), WarehouseId = line.WarehouseId,
                    ProductVariantId = line.ProductVariantId, Quantity = line.Quantity, Reference = $"Payment:{payment.PaymentId:N}/Order:{payment.OrderId:N}",
                    Reason = "Verified payment stock deduction" }, payment.ActorId, ct);
            }
            catch (InventoryException error) when (error.Code == "inventory_conflict" &&
                error.SafeMessage == "Insufficient stock, inactive inventory/warehouse or quantity limit exceeded.")
            {
                // The processor rolls back ALL lines, then records this event and the rejected Inbox entry atomically.
                var failureId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"inventory-unavailable:{envelope.MessageId:N}")).AsSpan(0, 16));
                return new InventoryUnavailableIntegrationEventV1(failureId, clock.GetUtcNow(), payment.PaymentId, payment.OrderId, "StockUnavailable");
            }
        }
        return null;
    }
}
