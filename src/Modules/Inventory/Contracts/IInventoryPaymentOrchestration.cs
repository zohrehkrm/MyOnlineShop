namespace MyOnlineShop.Inventory.Contracts;
public interface IInventoryPaymentOrchestration {
 Task ValidateAsync(PaymentSucceededIntegrationEventV1 payment,CancellationToken ct);
 Task ConfirmAsync(PaymentSucceededIntegrationEventV1 payment,CancellationToken ct);
}

