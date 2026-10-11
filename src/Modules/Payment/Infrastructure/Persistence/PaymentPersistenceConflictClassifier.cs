using MyOnlineShop.Payment.Domain;
namespace MyOnlineShop.Payment.Infrastructure.Persistence;

public static class PaymentPersistenceConflictClassifier
{
    // SQL diagnostic text is inspected only in memory. Persist only this bounded classification.
    public static PaymentReconciliationReason? Classify(int number, string diagnostic)
    {
        if (number is not (2601 or 2627)) return null;
        if (diagnostic.Contains("IX_Payments_Provider_ReceiptId", StringComparison.OrdinalIgnoreCase))
            return PaymentReconciliationReason.ProviderReceiptConflict;
        if (diagnostic.Contains("IX_Payments_Provider_ProviderReference", StringComparison.OrdinalIgnoreCase))
            return PaymentReconciliationReason.ProviderReferenceConflict;
        return PaymentReconciliationReason.PersistenceConstraintConflict;
    }
}
