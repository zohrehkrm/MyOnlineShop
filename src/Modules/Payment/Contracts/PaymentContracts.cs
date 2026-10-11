using System.ComponentModel.DataAnnotations;
namespace MyOnlineShop.Payment.Contracts;
public sealed class InitiatePaymentInput { public Guid OrderId { get; init; } public Guid IdempotencyKey { get; init; } }
public sealed class VerifyPaymentInput { [Required, StringLength(128)] public string ProviderReference { get; init; } = ""; }
public sealed record PaymentDto(Guid Id, Guid OrderId, string Status, string Fulfillment, decimal Amount, string Currency, string? CheckoutUrl,bool ReconciliationRequired = false);
public interface IPaymentCommands {
 Task<PaymentDto> InitiateAsync(Guid userId, InitiatePaymentInput input, CancellationToken ct);
 Task<PaymentDto> VerifyAsync(Guid? userId, Guid paymentId, string providerReference, CancellationToken ct);
}
public interface IPaymentQueries { Task<PaymentDto> GetMyAsync(Guid userId, Guid paymentId, CancellationToken ct); }

