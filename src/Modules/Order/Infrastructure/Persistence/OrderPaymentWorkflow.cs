using Microsoft.EntityFrameworkCore;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Application;
namespace MyOnlineShop.Order.Infrastructure.Persistence;
public sealed class OrderPaymentWorkflow(OrderDbContext db,TimeProvider clock,IRequestContext request) : IOrderPaymentWorkflow {
 public async Task<OrderPaymentSnapshot?> GetAsync(Guid id,CancellationToken ct) => await db.Orders.AsNoTracking().Where(x => x.Id == id)
  .Select(x => new OrderPaymentSnapshot(x.Id,x.UserId,x.Status.ToString(),x.PayableAmount,x.Currency,
   x.Items.Select(i => new OrderPaymentLine(i.ProductVariantId,i.Quantity,i.ProductKind)).ToList(),x.Shipping != null)).SingleOrDefaultAsync(ct);
 public Task BindAsync(Guid id,Guid payment,CancellationToken ct) => ChangeAsync(id,payment,"PaymentInitiated",ct);
 public Task ConfirmAsync(Guid id,Guid payment,CancellationToken ct) => ChangeAsync(id,payment,"PaymentFulfilled",ct);
 public Task FailAsync(Guid id,Guid payment,CancellationToken ct) => ChangeAsync(id,payment,"PaymentFailedOrRefundPending",ct);
 private async Task ChangeAsync(Guid id,Guid payment,string action,CancellationToken ct) {
  if(db.Database.CurrentTransaction is null) throw new InvalidOperationException("Payment orchestration requires the caller's SQL transaction.");
  var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == id,ct) ?? throw new InvalidOperationException("Payment order is missing.");
  await db.Entry(order).ReloadAsync(ct);
  try {
   if(action == "PaymentInitiated") order.BindPayment(payment);
   else if(action == "PaymentFulfilled") order.ConfirmPayment(payment,clock.GetUtcNow());
   else order.RejectPayment(payment,clock.GetUtcNow());
  } catch(OrderRuleException) { throw OrderException.Conflict(); }
  db.Audit.Add(OrderAudit.Record(id,order.UserId,action,clock.GetUtcNow(),request.CorrelationId));
  await db.SaveChangesAsync(ct);
 }
}

