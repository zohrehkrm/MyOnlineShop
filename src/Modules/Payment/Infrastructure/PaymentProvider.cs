using Microsoft.Extensions.Options;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Payment.Application;
namespace MyOnlineShop.Payment.Infrastructure;
// A real adapter must be explicitly registered. No production simulation or callback-based success.
public sealed class UnconfiguredPaymentProvider : IPaymentProvider {
 public string Name => "Unconfigured";
 public bool IsConfigured => false;
 public Task<ProviderInitiation> InitiateAsync(Guid id,decimal amount,string currency,CancellationToken ct) => throw PaymentException.Unavailable();
 public Task<ProviderVerification> VerifyAsync(Guid id,string reference,CancellationToken ct) => throw PaymentException.Unavailable();
}
public sealed class ConfiguredWarehouseAllocation(IOptions<PaymentOptions> options,IInventoryQueries inventory) : IPaymentAllocation {
 public async Task<IReadOnlyList<PaymentStockLine>> AllocateAsync(OrderPaymentSnapshot order,CancellationToken ct) {
  var physical = order.Lines.Where(x => x.ProductKind == "Physical").ToArray();
  if(physical.Length == 0) return [];
  var warehouse = options.Value.FulfillmentWarehouseId;
  if(warehouse is null || warehouse == Guid.Empty) throw PaymentException.Unavailable();
  var lines = new List<PaymentStockLine>();
  foreach(var line in physical) {
   var stock = await inventory.GetStockAsync(warehouse.Value,line.ProductVariantId,ct);
   if(!stock.IsActive || stock.AvailableQuantity < line.Quantity) throw PaymentException.Conflict();
   lines.Add(new(warehouse.Value,line.ProductVariantId,line.Quantity));
  }
  return lines;
 }
}

