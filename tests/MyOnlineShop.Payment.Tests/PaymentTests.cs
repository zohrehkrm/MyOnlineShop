using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Payment.Domain;
using MyOnlineShop.Payment.Infrastructure;
using MyOnlineShop.Wallet.Contracts;
using Xunit;
namespace MyOnlineShop.Payment.Tests;
// Application/control-flow tests with a simulated server provider. Not real gateway or SQL E2E validation.
public sealed class PaymentTests {
 [Fact] public async Task Reference_conflict_during_initiation_recovery_requires_reconciliation_without_success() {
  var h=new Harness(); h.Provider.FailAfterAccept=true;
  await Assert.ThrowsAsync<PaymentException>(()=>h.Start()); var p=Assert.Single(h.Store.Values);
  h.Provider.Status=ProviderPaymentStatus.Succeeded;
  h.Provider.Mutate=proof=> { p.RecoverVerifiedInitiation("conflicting-reference"); return proof; };
  await Assert.ThrowsAsync<PaymentException>(()=>h.Commands.VerifyAsync(null,p.Id,p.Id.ToString("N"),default));
  Assert.True(p.ReconciliationRequired); Assert.Empty(h.Outbox.Events); Assert.Equal("AwaitingPayment",h.Orders.Status);
 }
 [Fact] public async Task Server_verified_callback_recovers_gateway_acceptance_before_local_initiation_completion() {
  var h=new Harness(); h.Provider.FailAfterAccept=true;
  await Assert.ThrowsAsync<PaymentException>(()=>h.Start()); var p=Assert.Single(h.Store.Values);
  Assert.Equal(PaymentStatus.Created,p.Status); h.Provider.Status=ProviderPaymentStatus.Succeeded;
  await h.Commands.VerifyAsync(null,p.Id,p.Id.ToString("N"),default);
  Assert.Equal(PaymentStatus.Succeeded,p.Status); Assert.Single(h.Outbox.Events); Assert.Equal("AwaitingPayment",h.Orders.Status);
 }
 [Fact] public async Task Physical_allocation_requires_explicit_active_sufficient_warehouse_without_fallback() {
  var h=new Harness(); var snapshot=(await h.Orders.GetAsync(h.Order,default))!;
  var queries=new AllocationQueries();
  var config=Options.Create(new PaymentOptions());
  var allocator=new ConfiguredWarehouseAllocation(config,queries);
  await Assert.ThrowsAsync<PaymentException>(()=>allocator.AllocateAsync(snapshot,default)); Assert.Equal(0,queries.Calls);
  config.Value.FulfillmentWarehouseId=Guid.NewGuid(); queries.Active=false;
  await Assert.ThrowsAsync<PaymentException>(()=>allocator.AllocateAsync(snapshot,default));
  queries.Active=true; queries.Quantity=0;
  await Assert.ThrowsAsync<PaymentException>(()=>allocator.AllocateAsync(snapshot,default));
  queries.Quantity=1; var line=Assert.Single(await allocator.AllocateAsync(snapshot,default));
  Assert.Equal(config.Value.FulfillmentWarehouseId,line.WarehouseId); Assert.Equal(snapshot.Lines[0].ProductVariantId,line.ProductVariantId);
 }
 [Fact] public void Bound_order_cannot_be_cancelled_or_manually_failed_and_only_matching_payment_confirms() {
  var variant=Guid.NewGuid();
  var item=MyOnlineShop.Order.Domain.OrderItem.Snapshot(variant,"TEST","Unit","Digital",
   new MyOnlineShop.Pricing.Contracts.PriceLineQuote(variant,1,Guid.NewGuid(),"USD",1,null,null,0,0,1,1,[]),"USD");
  var order=MyOnlineShop.Order.Domain.Order.Create(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new string('A',64),"USD",
   DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,[item],null);
  order.Transition(MyOnlineShop.Order.Domain.OrderStatus.AwaitingPayment,DateTimeOffset.UtcNow); var payment=Guid.NewGuid(); order.BindPayment(payment);
  Assert.Throws<MyOnlineShop.Order.Domain.OrderRuleException>(()=>order.Transition(MyOnlineShop.Order.Domain.OrderStatus.Cancelled,DateTimeOffset.UtcNow));
  Assert.Throws<MyOnlineShop.Order.Domain.OrderRuleException>(()=>order.Transition(MyOnlineShop.Order.Domain.OrderStatus.Failed,DateTimeOffset.UtcNow));
  Assert.Throws<MyOnlineShop.Order.Domain.OrderRuleException>(()=>order.ConfirmPayment(Guid.NewGuid(),DateTimeOffset.UtcNow));
  order.ConfirmPayment(payment,DateTimeOffset.UtcNow); Assert.Equal(MyOnlineShop.Order.Domain.OrderStatus.Paid,order.Status);
  Assert.Throws<MyOnlineShop.Order.Domain.OrderRuleException>(()=>order.RejectPayment(payment,DateTimeOffset.UtcNow));
 }
 [Fact] public async Task Physical_order_without_captured_shipping_cannot_be_charged() {
  var h=new Harness(); h.Orders.HasShipping=false;
  await Assert.ThrowsAsync<PaymentException>(()=>h.Start()); Assert.Empty(h.Store.Values); Assert.Equal(0,h.Provider.Initiations);
 }
 [Fact] public void Gateway_credentials_require_secure_complete_configuration() {
  Assert.True(new PaymentGatewayOptions().IsValid());
  Assert.False(new PaymentGatewayOptions{Endpoint="http://provider.example.test",MerchantId="unit",Credential="test-only"}.IsValid());
  Assert.False(new PaymentGatewayOptions{Endpoint="https://provider.example.test",MerchantId="unit"}.IsValid());
 }
 [Fact] public async Task Simulated_provider_initiation_is_idempotent_and_server_owns_amount() {
  var h = new Harness(); var first = await h.Start(); var again = await h.Start();
  Assert.Equal(first.Id,again.Id); Assert.Single(h.Store.Values); Assert.Equal(100m,first.Amount); Assert.Equal(1,h.Provider.Initiations);
  Assert.Equal("AwaitingPayment",h.Orders.Status);
 }
 [Theory][InlineData("owner")][InlineData("key")][InlineData("status")]
 public async Task Invalid_owner_key_and_order_status_do_not_initiate(string field) {
  var h = new Harness(); if(field == "status") h.Orders.Status = "Cancelled";
  await Assert.ThrowsAsync<PaymentException>(() => h.Commands.InitiateAsync(field == "owner" ? Guid.NewGuid() : h.User,
   new() {OrderId=h.Order,IdempotencyKey=field == "key" ? Guid.Empty : h.Key},default));
  Assert.Empty(h.Store.Values); Assert.Equal(0,h.Provider.Initiations);
 }
 [Fact] public async Task Same_key_for_another_order_conflicts_without_another_charge() {
  var h = new Harness(); await h.Start();
  await Assert.ThrowsAsync<PaymentException>(() => h.Commands.InitiateAsync(h.User,new(){OrderId=Guid.NewGuid(),IdempotencyKey=h.Key},default));
  Assert.Equal(1,h.Provider.Initiations);
 }
 [Fact] public async Task Production_unconfigured_provider_fails_closed_before_binding_order() {
  var h = new Harness(); h.UseProvider(new UnconfiguredPaymentProvider());
  var e = await Assert.ThrowsAsync<PaymentException>(() => h.Start()); Assert.Equal(503,e.StatusCode);
  Assert.Empty(h.Store.Values); Assert.Null(h.Orders.Payment);
 }
 [Fact] public async Task Simulated_server_success_enqueues_once_but_does_not_confirm_before_inventory() {
  var h = new Harness(); var p = await h.Start(); h.Provider.Status = ProviderPaymentStatus.Succeeded;
  await h.Verify(p.Id); await h.Verify(p.Id);
  Assert.Equal(PaymentStatus.Succeeded,h.Store.Values.Single().Status); Assert.Single(h.Outbox.Events);
  Assert.Equal("AwaitingPayment",h.Orders.Status); Assert.Equal(2,h.Provider.Verifications);
 }
 [Theory][InlineData("amount")][InlineData("currency")][InlineData("merchant")][InlineData("reference")][InlineData("receipt")]
 public async Task Invalid_simulated_server_proof_cannot_emit_success(string field) {
  var h = new Harness(); var p = await h.Start(); h.Provider.Status = ProviderPaymentStatus.Succeeded;
  h.Provider.Mutate = proof => field switch { "amount" => proof with {Amount=101}, "currency" => proof with {Currency="EUR"},
   "merchant" => proof with {MerchantPaymentId=Guid.NewGuid()}, "reference" => proof with {Reference="other"}, _ => proof with {ReceiptId=null} };
  await Assert.ThrowsAnyAsync<Exception>(() => h.Verify(p.Id)); Assert.Empty(h.Outbox.Events);
  Assert.Equal(PaymentStatus.Initiated,h.Store.Values.Single().Status);
  Assert.True(h.Store.Values.Single().ReconciliationRequired);
 }
 [Theory][InlineData("")][InlineData("unknown")][InlineData("line\nbreak")]
 public async Task Invalid_callback_reference_does_not_call_provider(string reference) {
  var h = new Harness(); var p = await h.Start();
  await Assert.ThrowsAsync<PaymentException>(() => h.Commands.VerifyAsync(null,p.Id,reference,default));
  Assert.Equal(0,h.Provider.Verifications); Assert.Empty(h.Outbox.Events);
 }
 [Fact] public async Task Callback_pending_then_server_verified_success_is_safe() {
  var h = new Harness(); var p = await h.Start(); await h.Verify(p.Id); Assert.Empty(h.Outbox.Events);
  h.Provider.Status = ProviderPaymentStatus.Succeeded; await h.Commands.VerifyAsync(null,p.Id,h.Provider.Reference,default);
  Assert.Single(h.Outbox.Events);
 }
 [Fact] public async Task Failed_verification_fails_order_and_conflicting_delayed_success_requires_reconciliation() {
  var h = new Harness(); var p = await h.Start(); h.Provider.Status = ProviderPaymentStatus.Failed; await h.Verify(p.Id);
  Assert.Equal("Failed",h.Orders.Status); await h.Verify(p.Id);
  h.Provider.Status = ProviderPaymentStatus.Succeeded;
  await Assert.ThrowsAsync<PaymentException>(() => h.Verify(p.Id)); Assert.Empty(h.Outbox.Events);
  Assert.True(h.Store.Values.Single().ReconciliationRequired);
 }
 [Fact] public async Task Provider_unavailability_retains_recoverable_created_payment() {
  var h = new Harness(); h.Provider.Fail = true; await Assert.ThrowsAsync<PaymentException>(() => h.Start());
  Assert.Equal(PaymentStatus.Created,h.Store.Values.Single().Status);
  h.Provider.Fail = false; var result = await h.Start(); Assert.Equal(h.Store.Values.Single().Id,result.Id);
 }
 [Fact] public async Task Forged_success_event_is_rejected_before_any_deduction() {
  var h = new Harness(); var p = await h.Start(); h.Provider.Status = ProviderPaymentStatus.Succeeded; await h.Verify(p.Id);
  var ev = Assert.IsType<PaymentSucceededIntegrationEventV1>(Assert.Single(h.Outbox.Events));
  var stock = new Stock(); var consumer = new PaymentSucceededConsumer(stock,TimeProvider.System,h.Orchestration);
  await Assert.ThrowsAsync<InvalidMessageException>(() => consumer.HandleAsync(MessageEnvelope.From(ev with {Amount=1},"test"),default));
  Assert.Equal(0,stock.Calls);
 }
 [Fact] public async Task Simulated_success_duplicate_stock_event_deducts_and_confirms_once() {
  var h = new Harness(); var p = await h.Start(); h.Provider.Status = ProviderPaymentStatus.Succeeded; await h.Verify(p.Id);
  var ev = Assert.IsType<PaymentSucceededIntegrationEventV1>(Assert.Single(h.Outbox.Events));
  var stock = new Stock(); var consumer = new PaymentSucceededConsumer(stock,TimeProvider.System,h.Orchestration);
  await consumer.HandleAsync(MessageEnvelope.From(ev,"test"),default);
  await consumer.HandleAsync(MessageEnvelope.From(ev,"test"),default);
  Assert.Equal(1,stock.Calls); Assert.Equal("Paid",h.Orders.Status);
  Assert.Equal(PaymentFulfillment.Fulfilled,h.Store.Values.Single().Fulfillment);
  await Assert.ThrowsAsync<PaymentRuleException>(() => h.Orchestration.PrepareAsync(p.Id,h.Order,default));
 }
 [Fact] public async Task Shortage_returns_existing_failure_without_confirmation_and_claim_blocks_fulfillment() {
  var h = new Harness(); var p = await h.Start(); h.Provider.Status = ProviderPaymentStatus.Succeeded; await h.Verify(p.Id);
  var ev = Assert.IsType<PaymentSucceededIntegrationEventV1>(Assert.Single(h.Outbox.Events));
  var consumer = new PaymentSucceededConsumer(new Stock {Shortage=true},TimeProvider.System,h.Orchestration);
  Assert.IsType<InventoryUnavailableIntegrationEventV1>(await consumer.HandleAsync(MessageEnvelope.From(ev,"test"),default));
  Assert.Equal("AwaitingPayment",h.Orders.Status);
  Assert.Null(await h.Orchestration.GetSucceededPaymentAsync(p.Id,default));
  await h.Orchestration.PrepareAsync(p.Id,h.Order,default); await h.Orchestration.PrepareAsync(p.Id,h.Order,default);
  Assert.Equal("Failed",h.Orders.Status); Assert.NotNull(await h.Orchestration.GetSucceededPaymentAsync(p.Id,default));
  await Assert.ThrowsAsync<InvalidMessageException>(() => consumer.HandleAsync(MessageEnvelope.From(ev,"test"),default));
 }
 [Fact] public void Domain_protects_terminal_and_refund_transitions() {
  var p = PaymentRecord.Create(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),1,"USD","unit",[],DateTimeOffset.UtcNow,"test");
  Assert.Throws<PaymentRuleException>(() => p.Succeed("receipt",DateTimeOffset.UtcNow));
  p.Initiated("ref","https://provider.example.test/pay"); p.Succeed("receipt",DateTimeOffset.UtcNow);
  p.ClaimRefund(); p.Refunded(); p.Refunded(); Assert.Throws<PaymentRuleException>(p.Fulfill);
  Assert.Throws<PaymentRuleException>(() => p.Initiated("new","https://provider.example.test/pay"));
 }
}
internal sealed class Harness {
 public Guid User {get;} = Guid.NewGuid(); public Guid Order {get;}=Guid.NewGuid(); public Guid Key {get;}=Guid.NewGuid();
 public MemoryStore Store {get;}=new(); public MemoryUnit Unit {get;}=new(); public SimulatedProvider Provider {get;}=new();
 public TestOutbox Outbox {get;}=new(); public TestOrders Orders {get;}
 public TestWalletSafety WalletSafety {get;}=new();
 public PaymentCommands Commands {get;private set;}=null!; public PaymentOrchestration Orchestration {get;}
 public Harness(Guid? userId=null) { if(userId is {} user) User=user; Orders=new(Order,User); Orchestration=new(Store,Unit,Orders); UseProvider(Provider); }
 public void UseProvider(IPaymentProvider provider) => Commands=new(Store,Unit,provider,Orders,new Allocation(),Outbox,Options.Create(new PaymentOptions()),TimeProvider.System,new Request(),WalletSafety);
 public Task<PaymentDto> Start()=>Commands.InitiateAsync(User,new(){OrderId=Order,IdempotencyKey=Key},default);
 public Task<PaymentDto> Verify(Guid id)=>Commands.VerifyAsync(User,id,Provider.Reference,default);
}
internal sealed class MemoryStore : IPaymentStore {
 public List<PaymentRecord> Values {get;}=[];
 public Task<PaymentRecord?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult(Values.SingleOrDefault(x=>x.Id==id));
 public Task<PaymentRecord?> ByOrderAsync(Guid id,CancellationToken ct)=>Task.FromResult(Values.SingleOrDefault(x=>x.OrderId==id));
 public Task<PaymentRecord?> ByKeyAsync(Guid user,Guid key,CancellationToken ct)=>Task.FromResult(Values.SingleOrDefault(x=>x.UserId==user&&x.IdempotencyKey==key));
 public void Add(PaymentRecord p)=>Values.Add(p); public Task SaveAsync(CancellationToken ct)=>Task.CompletedTask;
}
internal sealed class MemoryUnit : IPaymentUnitOfWork {
 public Queue<Exception> Failures {get;}=new();
 public Task<T> ExecuteAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken ct) {
  if(Failures.TryDequeue(out var failure)) throw failure;
  return action(ct);
 }
 public Task LockAsync(Guid order,CancellationToken ct)=>Task.CompletedTask;
}
internal sealed class Request : IRequestContext { public string CorrelationId=>"payment-test"; }
internal sealed class TestOutbox : IOutboxWriter {
 public List<IIntegrationEvent> Events {get;}=[];
 public Task EnqueueAsync(IIntegrationEvent value,string correlation,CancellationToken ct) { Events.Add(value); return Task.CompletedTask; }
}
internal sealed class TestOrders(Guid id,Guid user) : IOrderPaymentWorkflow {
 public string Status {get;set;}="AwaitingPayment"; public bool HasShipping {get;set;}=true; public Guid? Payment {get;private set;}
 public Guid Variant {get;}=Guid.NewGuid();
 public Task<OrderPaymentSnapshot?> GetAsync(Guid order,CancellationToken ct)=>Task.FromResult<OrderPaymentSnapshot?>(order==id ? new(id,user,Status,100,"USD",[new(Variant,1,"Physical")],HasShipping) : null);
 public Task BindAsync(Guid order,Guid payment,CancellationToken ct) {Payment=payment;return Task.CompletedTask;}
 public Task ConfirmAsync(Guid order,Guid payment,CancellationToken ct) {if(Payment!=payment || Status=="Failed") throw PaymentException.Conflict();Status="Paid";return Task.CompletedTask;}
 public Task FailAsync(Guid order,Guid payment,CancellationToken ct) {Status="Failed";return Task.CompletedTask;}
}
internal sealed class Allocation : IPaymentAllocation {
 public static Guid Warehouse {get;}=Guid.NewGuid();
 public Task<IReadOnlyList<PaymentStockLine>> AllocateAsync(OrderPaymentSnapshot order,CancellationToken ct)=>Task.FromResult<IReadOnlyList<PaymentStockLine>>(order.Lines.Select(x=>new PaymentStockLine(Warehouse,x.ProductVariantId,x.Quantity)).ToArray());
}
internal sealed class SimulatedProvider : IPaymentProvider {
 public string Name=>"SimulatedTestOnly"; public string Reference {get;private set;} = "";
 public ProviderPaymentStatus Status {get;set;}=ProviderPaymentStatus.Pending; public bool Fail {get;set;} public bool FailAfterAccept {get;set;}
 public int Initiations {get;private set;} public int Verifications {get;private set;}
 public Func<ProviderVerification,ProviderVerification>? Mutate {get;set;}
 public Func<ProviderVerification,Task<ProviderVerification>>? VerifyHook {get;set;}
 public Action? AfterInitiation {get;set;}
 private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,(decimal Amount,string Currency)> initiated=new();
 public Task<ProviderInitiation> InitiateAsync(Guid id,decimal amount,string currency,CancellationToken ct) {
  if(Fail) throw new IOException("provider unavailable"); Initiations++; initiated.TryAdd(id,(amount,currency)); Reference=id.ToString("N");
  if(FailAfterAccept) throw new IOException("Gateway accepted; local response unavailable.");
  AfterInitiation?.Invoke();
  return Task.FromResult(new ProviderInitiation(id.ToString("N"),"https://provider.example.test/pay/"+id.ToString("N")));
 }
 // Backend assertions are independent of callback input; only records initiated through this fake can verify.
 public async Task<ProviderVerification> VerifyAsync(Guid id,string reference,CancellationToken ct) {
  Verifications++; if(Fail || !initiated.TryGetValue(id,out var value)) throw new IOException("provider unavailable");
  var proof=new ProviderVerification(Status,id,id.ToString("N"),value.Amount,value.Currency,"receipt-"+id.ToString("N"));
  if(VerifyHook is not null) return await VerifyHook(proof);
  return Mutate is null ? proof : Mutate(proof);
 }
}
internal sealed class TestWalletSafety : IWalletPaymentSafety {
 public string? Currency {get;set;}
 public bool Fail {get;set;}
 public Task<string?> GetPrimaryCurrencyAsync(Guid user,CancellationToken ct) => Fail ? throw new IOException("Wallet lookup unavailable") : Task.FromResult(Currency);
 public Task<WalletTransactionDto?> FindPostedCreditAsync(Guid user,Guid key,CancellationToken ct) => Task.FromResult<WalletTransactionDto?>(null);
}
internal sealed class Stock : IInventoryDeduction {
 public bool Shortage {get;set;} public int Calls {get;private set;} private readonly Dictionary<Guid,MovementDto> results=[];
 public Task<MovementDto> DeductAsync(StockOperationInput input,Guid actor,CancellationToken ct) {
  if(Shortage) throw InventoryException.Conflict("Insufficient stock, inactive inventory/warehouse or quantity limit exceeded.");
  if(results.TryGetValue(input.OperationId,out var prior)) return Task.FromResult(prior);
  Calls++; var result=new MovementDto(Guid.NewGuid(),input.OperationId,Guid.NewGuid(),input.WarehouseId,input.ProductVariantId,-input.Quantity,1,0,"Sale",input.Reference,input.Reason,actor,DateTimeOffset.UtcNow,"test");
  results[input.OperationId]=result;return Task.FromResult(result);
 }
}
internal sealed class AllocationQueries : IInventoryQueries {
 public bool Active {get;set;}=true; public long Quantity {get;set;}=1; public int Calls {get;private set;}
 public Task<StockDto> GetStockAsync(Guid warehouse,Guid variant,CancellationToken ct) { Calls++; return Task.FromResult(new StockDto(Guid.NewGuid(),warehouse,variant,Quantity,Quantity,0,Active)); }
 public Task<WarehouseDto> GetWarehouseAsync(Guid id,CancellationToken ct)=>throw new NotSupportedException();
 public Task<InventoryPage<WarehouseDto>> ListWarehousesAsync(InventoryQuery q,CancellationToken ct)=>throw new NotSupportedException();
 public Task<InventoryPage<StockDto>> ListStockAsync(InventoryQuery q,CancellationToken ct)=>throw new NotSupportedException();
 public Task<InventoryPage<MovementDto>> ListMovementsAsync(InventoryQuery q,CancellationToken ct)=>throw new NotSupportedException();
 public Task<InventoryPage<ReceiptDto>> ListReceiptsAsync(InventoryQuery q,CancellationToken ct)=>throw new NotSupportedException();
 public Task<InventoryPage<AdjustmentDto>> ListAdjustmentsAsync(InventoryQuery q,CancellationToken ct)=>throw new NotSupportedException();
}

