using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Infrastructure;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Infrastructure;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Payment.Domain;
using MyOnlineShop.Payment.Infrastructure;
using MyOnlineShop.Payment.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Refund.Application;
using MyOnlineShop.Refund.Infrastructure;
using MyOnlineShop.Refund.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Infrastructure;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using Xunit;
using OrderEntity = MyOnlineShop.Order.Domain.Order;
namespace MyOnlineShop.Payment.Tests;
public sealed class PaymentSqlFactAttribute : FactAttribute {
 public PaymentSqlFactAttribute() { if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYMENT_TEST_SQL_SERVER")))
  Skip="SQL Server unavailable. Requires separately authorized disposable SQL Server; simulated provider only, not real gateway validation."; }
}
public sealed class PaymentSqlTests : IClassFixture<PaymentSqlFixture> {
 private readonly PaymentSqlFixture f;
 public PaymentSqlTests(PaymentSqlFixture fixture)=>f=fixture;
 [PaymentSqlFact] public async Task Concurrent_simulated_provider_callbacks_commit_one_success_event_and_wait_for_inventory() {
  var stock=await f.Stock(1); var p=await f.Start(stock);
  await Task.WhenAll(f.Verify(p.Id),f.Verify(p.Id));
  using var scope=f.Provider.CreateScope(); var s=scope.ServiceProvider;
  var db=s.GetRequiredService<PaymentDbContext>(); var row=await db.Payments.SingleAsync(x=>x.Id==p.Id);
  Assert.Equal(PaymentStatus.Succeeded,row.Status);
  Assert.Equal(1,await s.GetRequiredService<MessagingDbContext>().Outbox.CountAsync(x=>x.Id==p.Id));
  Assert.Equal(MyOnlineShop.Order.Domain.OrderStatus.AwaitingPayment,(await s.GetRequiredService<OrderDbContext>().Orders.SingleAsync(x=>x.Id==p.OrderId)).Status);
 }
 [PaymentSqlFact] public async Task Payment_and_success_outbox_roll_back_together_then_retry_verifies_same_payment() {
  var p=await f.Start(await f.Stock(1)); f.Fault.Payment=p.Id;
  await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Verify(p.Id));
  using(var scope=f.Provider.CreateScope()) {
   Assert.Equal(PaymentStatus.Initiated,(await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Payments.SingleAsync(x=>x.Id==p.Id)).Status);
   Assert.False(await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Outbox.AnyAsync(x=>x.Id==p.Id));
  }
  await f.Verify(p.Id);
 }
 [PaymentSqlFact] public async Task Inventory_race_confirms_one_order_and_durable_shortage_refunds_other_once() {
  var stock=await f.Stock(1); var a=await f.Start(stock); var b=await f.Start(stock); await f.Verify(a.Id); await f.Verify(b.Id);
  var ea=await f.Event(a.Id); var eb=await f.Event(b.Id);
  var results=await Task.WhenAll(f.Deliver(ea,"inventory.payment-succeeded.v1"),f.Deliver(eb,"inventory.payment-succeeded.v1"));
  Assert.Single(results,x=>x==ConsumptionDisposition.Processed); Assert.Single(results,x=>x==ConsumptionDisposition.Rejected);
  var loser=results[0]==ConsumptionDisposition.Rejected ? a : b;
  using(var scope=f.Provider.CreateScope()) {
   var s=scope.ServiceProvider;
   Assert.Equal(0,(await s.GetRequiredService<InventoryDbContext>().Stocks.SingleAsync(x=>x.WarehouseId==stock.Warehouse&&x.ProductVariantId==stock.Variant)).Quantity);
   Assert.Equal(1,await s.GetRequiredService<OrderDbContext>().Orders.CountAsync(x=>(x.Id==a.OrderId||x.Id==b.OrderId)&&x.Status==MyOnlineShop.Order.Domain.OrderStatus.Paid));
  }
  var failure=await f.EventByType(loser.Id,"inventory.unavailable");
  await f.Deliver(failure,"refund.inventory-unavailable.v1");
  using(var scope=f.Provider.CreateScope()) { await scope.ServiceProvider.GetRequiredService<RefundScheduler>().ScheduleAsync(default); }
  var due=await f.EventByType(loser.Id,"refunds.due");
  Assert.Equal(ConsumptionDisposition.Processed,await f.Deliver(due,"refund.due.v1"));
  Assert.Equal(ConsumptionDisposition.Duplicate,await f.Deliver(due,"refund.due.v1"));
  using(var scope=f.Provider.CreateScope()) {
   Assert.Equal(1,await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Ledger.CountAsync(x=>x.ReferenceId==loser.Id.ToString("N")));
   Assert.Equal(MyOnlineShop.Order.Domain.OrderStatus.Failed,(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.SingleAsync(x=>x.Id==loser.OrderId)).Status);
  }
 }
 [PaymentSqlFact] public async Task Inbox_failure_rolls_back_stock_order_confirmation_and_payment_fulfillment() {
  var stock=await f.Stock(1); var p=await f.Start(stock); await f.Verify(p.Id); f.Fault.Inbox=p.Id;
  await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Deliver(awaited:null,consumer:"inventory.payment-succeeded.v1",payment:p.Id));
  using(var scope=f.Provider.CreateScope()) {
   var s=scope.ServiceProvider;
   Assert.Equal(1,(await s.GetRequiredService<InventoryDbContext>().Stocks.SingleAsync(x=>x.WarehouseId==stock.Warehouse&&x.ProductVariantId==stock.Variant)).Quantity);
   Assert.Equal(PaymentFulfillment.Pending,(await s.GetRequiredService<PaymentDbContext>().Payments.SingleAsync(x=>x.Id==p.Id)).Fulfillment);
   Assert.Equal(MyOnlineShop.Order.Domain.OrderStatus.AwaitingPayment,(await s.GetRequiredService<OrderDbContext>().Orders.SingleAsync(x=>x.Id==p.OrderId)).Status);
  }
  Assert.Equal(ConsumptionDisposition.Processed,await f.Deliver(await f.Event(p.Id),"inventory.payment-succeeded.v1"));
 }
}
public sealed class PaymentSqlFixture : IAsyncLifetime {
 private readonly string? server=Environment.GetEnvironmentVariable("PAYMENT_TEST_SQL_SERVER");
 private readonly string database="MyOnlineShop_PaymentTests_"+Guid.NewGuid().ToString("N");
 private bool created;
 public ServiceProvider Provider {get;private set;}=null!;
 internal SqlFault Fault {get;}=new();
 private readonly SimulatedProvider gateway=new(){Status=ProviderPaymentStatus.Succeeded};
 public async Task InitializeAsync() {
  if(string.IsNullOrWhiteSpace(server)) return;
  var cs=new SqlConnectionStringBuilder(server){InitialCatalog="master",MultipleActiveResultSets=false};
  if(cs.DataSource.Contains("localdb",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB prohibited.");
  await using(var connection=new SqlConnection(cs.ConnectionString)) {
   await connection.OpenAsync(); await using var cmd=connection.CreateCommand(); cmd.CommandText=$"CREATE DATABASE [{database}]"; await cmd.ExecuteNonQueryAsync(); created=true;
  }
  cs.InitialCatalog=database;
  var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
   ["ConnectionStrings:SqlServer"]=cs.ConnectionString,["RefundProcessing:DelayMinutes"]="0"
  }).Build();
  var services=new ServiceCollection().AddLogging();
  services.AddFoundationInfrastructure(config); services.AddOrderInfrastructure(config); services.AddInventoryInfrastructure(config);
  services.AddPaymentInfrastructure(config); services.AddWalletInfrastructure(config); services.AddRefundInfrastructure(config);
  services.AddSingleton<IPaymentProvider>(gateway); services.AddScoped<IPaymentAllocation,Allocation>();
  services.AddSingleton<ICatalogVariantReferences,SqlCatalog>(); services.AddSingleton<IIdentityQueries,SqlIdentity>();
  services.AddScoped<IRequestContext,Request>();
  services.AddDbContext<MessagingDbContext>((_,options)=>options.AddInterceptors(Fault));
  Provider=services.BuildServiceProvider(); using var scope=Provider.CreateScope(); var s=scope.ServiceProvider;
  await s.GetRequiredService<MessagingDbContext>().Database.MigrateAsync(); await s.GetRequiredService<OrderDbContext>().Database.MigrateAsync();
  await s.GetRequiredService<InventoryDbContext>().Database.MigrateAsync(); await s.GetRequiredService<PaymentDbContext>().Database.MigrateAsync();
  await s.GetRequiredService<WalletDbContext>().Database.MigrateAsync(); await s.GetRequiredService<RefundDbContext>().Database.MigrateAsync();
 }
 public async Task<(Guid Warehouse,Guid Variant)> Stock(long quantity) {
  using var scope=Provider.CreateScope(); var commands=scope.ServiceProvider.GetRequiredService<IInventoryCommands>();
  // The test allocation's explicitly selected warehouse is used; no production allocation heuristic.
  var warehouse=await commands.CreateWarehouseAsync(new(){Code=Guid.NewGuid().ToString("N"),Name="Payment SQL fixture"},default);
  var variant=Guid.NewGuid();
  await commands.ReceiveAsync(new(){OperationId=Guid.NewGuid(),WarehouseId=warehouse.Id,ProductVariantId=variant,Quantity=quantity,Reference="fixture",Reason="fixture"},Guid.NewGuid(),default);
  return(warehouse.Id,variant);
 }
 public async Task<PaymentDto> Start((Guid Warehouse,Guid Variant) stock) {
  using var scope=Provider.CreateScope(); var s=scope.ServiceProvider; var user=Guid.NewGuid();
  var line=MyOnlineShop.Order.Domain.OrderItem.Snapshot(stock.Variant,"TEST","Fixture","Physical",
   new PriceLineQuote(stock.Variant,1,Guid.NewGuid(),"USD",100,null,null,0,0,100,100,[]),"USD");
  var shipping=new MyOnlineShop.Shipping.Contracts.ShippingQuoteSnapshot {ShippingMethodId=Guid.NewGuid(),MethodCode="fixture",MethodName="Fixture",
   Currency="USD",QuotedAtUtc=DateTimeOffset.UtcNow,Address=new("SQL Fixture","+15550102030","State","City","Street","12345","US",null,null)};
  var order=OrderEntity.Create(user,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new string('A',64),"USD",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,[line],null,shipping);
  order.Transition(MyOnlineShop.Order.Domain.OrderStatus.AwaitingPayment,DateTimeOffset.UtcNow);
  var db=s.GetRequiredService<OrderDbContext>(); db.Orders.Add(order); await db.SaveChangesAsync();
  // Only allocation is simulated; payment success is obtained through the real commands and fake gateway verification.
  var unit=s.GetRequiredService<IPaymentUnitOfWork>();
  var commands=new PaymentCommands(s.GetRequiredService<IPaymentStore>(),unit,gateway,s.GetRequiredService<IOrderPaymentWorkflow>(),
   new SqlAllocation(stock.Warehouse),s.GetRequiredService<IOutboxWriter>(),Microsoft.Extensions.Options.Options.Create(new PaymentOptions()),TimeProvider.System,new Request(),s.GetRequiredService<MyOnlineShop.Wallet.Contracts.IWalletPaymentSafety>());
  return await commands.InitiateAsync(user,new(){OrderId=order.Id,IdempotencyKey=Guid.NewGuid()},default);
 }
 public async Task Verify(Guid id) {
  using var scope=Provider.CreateScope();
  await scope.ServiceProvider.GetRequiredService<IPaymentCommands>().VerifyAsync(null,id,id.ToString("N"),default);
 }
 public async Task<MessageEnvelope> Event(Guid id) {
  using var scope=Provider.CreateScope(); return(await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Outbox.AsNoTracking().SingleAsync(x=>x.Id==id)).Envelope();
 }
 public async Task<MessageEnvelope> EventByType(Guid payment,string type) {
  using var scope=Provider.CreateScope(); var s=scope.ServiceProvider;
  var refund=await s.GetRequiredService<RefundDbContext>().Refunds.AsNoTracking().SingleOrDefaultAsync(x=>x.PaymentId==payment);
  var rows=await s.GetRequiredService<MessagingDbContext>().Outbox.AsNoTracking().Where(x=>x.EventType==type).ToListAsync();
  return rows.Single(x=>x.Payload.Contains(payment.ToString()) || refund is not null && x.Payload.Contains(refund.Id.ToString())).Envelope();
 }
 public async Task<ConsumptionDisposition> Deliver(MessageEnvelope? awaited,string consumer,Guid? payment=null) {
  var envelope=awaited??await Event(payment!.Value);
  using var scope=Provider.CreateScope(); var s=scope.ServiceProvider;
  return await s.GetRequiredService<IMessageProcessor>().ProcessAsync(s.GetServices<IMessageConsumer>().Single(x=>x.ConsumerName==consumer),envelope,default);
 }
 public async Task DisposeAsync() {
  if(Provider is not null) await Provider.DisposeAsync(); if(!created) return;
  const string prefix="MyOnlineShop_PaymentTests_";
  if(!database.StartsWith(prefix,StringComparison.Ordinal) || !Guid.TryParseExact(database[prefix.Length..],"N",out _)) throw new InvalidOperationException("Unsafe fixture database.");
  SqlConnection.ClearAllPools(); var cs=new SqlConnectionStringBuilder(server){InitialCatalog="master"};
  await using var connection=new SqlConnection(cs.ConnectionString); await connection.OpenAsync(); await using var cmd=connection.CreateCommand();
  cmd.CommandText=$"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]"; await cmd.ExecuteNonQueryAsync();
 }
}
internal sealed class SqlAllocation(Guid warehouse) : IPaymentAllocation {
 public Task<IReadOnlyList<PaymentStockLine>> AllocateAsync(OrderPaymentSnapshot order,CancellationToken ct)=>
  Task.FromResult<IReadOnlyList<PaymentStockLine>>(order.Lines.Where(x=>x.ProductKind=="Physical").Select(x=>new PaymentStockLine(warehouse,x.ProductVariantId,x.Quantity)).ToArray());
}
internal sealed class SqlCatalog : ICatalogVariantReferences {
 public Task<CatalogVariantReference?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult<CatalogVariantReference?>(new(id,"TEST","Physical",true));
}
internal sealed class SqlIdentity : IIdentityQueries {
 public Task<UserDto> GetUserAsync(Guid id,CancellationToken ct)=>Task.FromResult(new UserDto(id,"SQL","Fixture","fixture@example.test",null,true,["Customer"]));
 public Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<RoleDto>>([]);
}
internal sealed class SqlFault : SaveChangesInterceptor {
 public Guid? Payment {get;set;} public Guid? Inbox {get;set;}
 public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default) {
  if(data.Context is MessagingDbContext db) {
   if(Payment is {} p && db.ChangeTracker.Entries<OutboxMessage>().Any(x=>x.Entity.Id==p && x.State==EntityState.Added)) { Payment=null;throw new InvalidOperationException("Injected Outbox failure."); }
   if(Inbox is {} i && db.ChangeTracker.Entries<InboxMessage>().Any(x=>x.Entity.MessageId==i && x.State==EntityState.Added)) { Inbox=null;throw new InvalidOperationException("Injected Inbox failure."); }
  }
  return ValueTask.FromResult(result);
 }
}

