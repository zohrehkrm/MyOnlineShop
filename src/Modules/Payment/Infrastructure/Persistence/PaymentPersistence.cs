using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Payment.Domain;
namespace MyOnlineShop.Payment.Infrastructure.Persistence;
public sealed class PaymentStore(PaymentDbContext db) : IPaymentStore,IPaymentQueries {
 public async Task<PaymentRecord?> GetAsync(Guid id,CancellationToken ct) {
  var tracked = db.Payments.Local.SingleOrDefault(x => x.Id == id);
  if(tracked is not null) await db.Entry(tracked).ReloadAsync(ct);
  return await db.Payments.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.Id == id,ct);
 }
 public Task<PaymentRecord?> ByOrderAsync(Guid id,CancellationToken ct) => db.Payments.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.OrderId == id,ct);
 public Task<PaymentRecord?> ByKeyAsync(Guid user,Guid key,CancellationToken ct) => db.Payments.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.UserId == user && x.IdempotencyKey == key,ct);
 public void Add(PaymentRecord p) => db.Payments.Add(p);
 public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
 public async Task<PaymentDto> GetMyAsync(Guid user,Guid id,CancellationToken ct) {
  if(user == Guid.Empty) throw PaymentException.NotFound();
  var value = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == user,ct) ?? throw PaymentException.NotFound();
  return value.Dto();
 }
}
public sealed class PaymentUnitOfWork(PaymentDbContext db,IEnumerable<ILocalSqlTransactionParticipant> participants) : IPaymentUnitOfWork {
 public async Task<T> ExecuteAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken ct) {
  try {
   if(db.Database.CurrentTransaction is not null) return await action(ct);
   return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
    db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var joined = new List<ILocalSqlTransactionParticipant>();
    try {
     foreach(var name in new[]{"ordering","messaging"}) {
      var participant = participants.Single(x => x.Name == name); joined.Add(participant);
      await participant.EnlistAsync(db.Database.GetDbConnection(),transaction.GetDbTransaction(),ct);
     }
     var result = await action(ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
    } finally { foreach(var participant in joined.AsEnumerable().Reverse()) await participant.DetachAsync(CancellationToken.None); }
   });
  } catch(PaymentRuleException) { throw PaymentException.Conflict(); }
  catch(MyOnlineShop.Pricing.Contracts.MoneyRuleException) { throw PaymentException.Invalid(); }
  catch(DbUpdateConcurrencyException) { throw PaymentException.Conflict(); }
  catch(DbUpdateException e) when(e.InnerException is SqlException {Number:2601 or 2627} sql) {
   throw new PaymentPersistenceConflictException(PaymentPersistenceConflictClassifier.Classify(sql.Number,sql.Message)!.Value,e);
  }
  catch(DbUpdateException e) when(e.InnerException is SqlException {Number:547}) {
   throw new PaymentException("payment_conflict",409,"Payment conflicts with the current purchase state.",e);
  }
  catch(SqlException e) when(e.Number == 51008) { throw PaymentException.Conflict(); }
 }
 public Task LockAsync(Guid orderId,CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
  DECLARE @result int;
  EXEC @result = sys.sp_getapplock @Resource={"PaymentOrder:" + orderId.ToString("N")}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
  IF @result < 0 THROW 51008, 'Payment is busy.', 1;
  """,ct);
}

