using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Domain;
using MyOnlineShop.Payment.Infrastructure.Persistence;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using Xunit;
namespace MyOnlineShop.Payment.Tests;

public sealed class PaymentSafetyTests
{
 [Fact] public async Task Conflicting_initiation_response_is_held_instead_of_returning_a_successful_replay() {
  var h=new Harness(); h.Provider.AfterInitiation=()=>h.Store.Values.Single().RecoverVerifiedInitiation("conflicting-reference");
  await Assert.ThrowsAsync<PaymentException>(()=>h.Start());
  Assert.Equal(PaymentReconciliationReason.ProviderReferenceConflict,h.Store.Values.Single().ReconciliationReason);
  Assert.Empty(h.Outbox.Events);
 }
 [Theory]
 [InlineData(ProviderPaymentStatus.Pending)] [InlineData(ProviderPaymentStatus.Failed)]
 [InlineData(ProviderPaymentStatus.Reversed)] [InlineData(ProviderPaymentStatus.Chargeback)]
 public async Task Authoritative_conflict_after_success_sets_hold_without_second_event(ProviderPaymentStatus status) {
  var h=new Harness(); var p=await h.Start(); h.Provider.Status=ProviderPaymentStatus.Succeeded; await h.Verify(p.Id);
  h.Provider.Status=status;
  await Assert.ThrowsAsync<PaymentException>(()=>h.Verify(p.Id));
  Assert.True(h.Store.Values.Single().ReconciliationRequired); Assert.Single(h.Outbox.Events);
  Assert.Equal(PaymentStatus.Succeeded,h.Store.Values.Single().Status);
  await Assert.ThrowsAsync<InvalidMessageException>(()=>h.Orchestration.ValidateAsync(h.Store.Values.Single().SuccessEvent(),default));
 }
 [Fact] public async Task Different_authoritative_success_receipt_is_not_an_ordinary_duplicate() {
  var h=new Harness(); var p=await h.Start(); h.Provider.Status=ProviderPaymentStatus.Succeeded; await h.Verify(p.Id);
  h.Provider.Mutate=proof=>proof with {ReceiptId="other-receipt"};
  await Assert.ThrowsAsync<PaymentException>(()=>h.Verify(p.Id)); Assert.True(h.Store.Values.Single().ReconciliationRequired); Assert.Single(h.Outbox.Events);
 }
 [Fact] public async Task Conflict_that_arrives_after_another_verification_commits_success_is_held() {
  var h=new Harness(); var p=await h.Start(); h.Provider.Status=ProviderPaymentStatus.Failed;
  h.Provider.VerifyHook=async proof=> {
   h.Provider.VerifyHook=null; h.Provider.Status=ProviderPaymentStatus.Succeeded;
   await h.Verify(p.Id); // Another real application verification succeeds through the simulated server provider.
   return proof;
  };
  await Assert.ThrowsAsync<PaymentException>(()=>h.Verify(p.Id)); Assert.True(h.Store.Values.Single().ReconciliationRequired); Assert.Single(h.Outbox.Events);
 }
 [Fact] public async Task Created_reconciliation_hold_blocks_gateway_reinitiation_and_domain_recovery() {
  var h=new Harness(); h.Provider.FailAfterAccept=true; await Assert.ThrowsAsync<PaymentException>(()=>h.Start());
  var p=h.Store.Values.Single(); p.RequireReconciliation(); var calls=h.Provider.Initiations;
  await Assert.ThrowsAsync<PaymentException>(()=>h.Start()); Assert.Equal(calls,h.Provider.Initiations);
  Assert.Throws<PaymentRuleException>(()=>p.Initiated("ref","https://provider.example.test/pay"));
  Assert.Throws<PaymentRuleException>(()=>p.RecoverVerifiedInitiation("ref"));
 }
 [Fact] public async Task Held_success_blocks_new_refund_claim_but_retains_held_evidence_for_existing_credit_recovery() {
  var h=new Harness(); var p=await h.Start(); h.Provider.Status=ProviderPaymentStatus.Succeeded; await h.Verify(p.Id);
  var record=h.Store.Values.Single(); record.RequireReconciliation();
  Assert.Throws<PaymentRuleException>(record.ClaimRefund);
  // Separate successful payment already claimed before the hold.
  var other=new Harness(); var q=await other.Start(); other.Provider.Status=ProviderPaymentStatus.Succeeded; await other.Verify(q.Id);
  await other.Orchestration.PrepareAsync(q.Id,other.Order,default); other.Store.Values.Single().RequireReconciliation();
  Assert.True((await other.Orchestration.GetSucceededPaymentAsync(q.Id,default))!.ReconciliationRequired);
  var consumer=new MyOnlineShop.Refund.Application.RefundDueConsumer(null!,[other.Orchestration]);
  Assert.Contains("payment",consumer.TransactionParticipants);
 }
 [Fact] public async Task Known_incompatible_wallet_currency_rejects_before_binding_or_charging() {
  var h=new Harness(); h.WalletSafety.Currency="EUR";
  var error=await Assert.ThrowsAsync<PaymentException>(()=>h.Start()); Assert.Equal("payment_refund_currency",error.Code);
  Assert.Equal(0,h.Provider.Initiations); Assert.Empty(h.Store.Values); Assert.Null(h.Orders.Payment);
 }
 [Fact] public async Task Compatible_or_absent_primary_wallet_does_not_select_another_refund_destination() {
  var h=new Harness(); h.WalletSafety.Currency="USD"; await h.Start(); Assert.Equal(1,h.Provider.Initiations);
  var other=new Harness(); await other.Start(); Assert.Equal(1,other.Provider.Initiations);
 }
 [Fact] public async Task Retry_checks_wallet_currency_again_before_another_gateway_call() {
  var h=new Harness(); h.Provider.Fail=true; await Assert.ThrowsAsync<PaymentException>(()=>h.Start());
  h.Provider.Fail=false; h.WalletSafety.Currency="EUR";
  await Assert.ThrowsAsync<PaymentException>(()=>h.Start()); Assert.Equal(0,h.Provider.Initiations);
 }
 [Fact] public async Task Unavailable_wallet_currency_lookup_does_not_charge() {
  var h=new Harness(); h.WalletSafety.Fail=true;
  await Assert.ThrowsAsync<IOException>(()=>h.Start()); Assert.Equal(0,h.Provider.Initiations); Assert.Empty(h.Store.Values);
 }
 [Theory]
 [InlineData(2601,"IX_Payments_Provider_ReceiptId",PaymentReconciliationReason.ProviderReceiptConflict)]
 [InlineData(2627,"IX_Payments_Provider_ProviderReference",PaymentReconciliationReason.ProviderReferenceConflict)]
 [InlineData(2601,"other",PaymentReconciliationReason.PersistenceConstraintConflict)]
 public void Sql_conflict_classification_retains_only_safe_reason(int number,string index,PaymentReconciliationReason expected) {
  Assert.Equal(expected,PaymentPersistenceConflictClassifier.Classify(number,index+" sensitive-payload=never-retain"));
  Assert.Null(PaymentPersistenceConflictClassifier.Classify(547,index));
 }
 [Fact] public async Task Provider_receipt_conflict_is_recorded_in_a_followup_unit_and_original_error_preserved() {
  var h=new Harness(); var p=await h.Start(); h.Provider.Status=ProviderPaymentStatus.Succeeded;
  var original=new IOException("sensitive server diagnostic");
  var conflict=new PaymentPersistenceConflictException(PaymentReconciliationReason.ProviderReceiptConflict,original);
  h.Unit.Failures.Enqueue(conflict); // Inject before mutation; this test does not simulate SQL rollback.
  var error=await Assert.ThrowsAsync<PaymentPersistenceConflictException>(()=>h.Verify(p.Id));
  Assert.Same(original,error.InnerException); Assert.DoesNotContain(original.Message,error.SafeMessage);
  Assert.True(h.Store.Values.Single().ReconciliationRequired); Assert.Equal(conflict.Reason,h.Store.Values.Single().ReconciliationReason);
  Assert.Empty(h.Outbox.Events);
 }
 [Fact] public async Task Initiation_reference_conflict_is_recorded_after_provider_acceptance() {
  var h=new Harness(); h.Provider.AfterInitiation=()=>h.Unit.Failures.Enqueue(new PaymentPersistenceConflictException(
   PaymentReconciliationReason.ProviderReferenceConflict,new IOException("server diagnostic")));
  await Assert.ThrowsAsync<PaymentPersistenceConflictException>(()=>h.Start());
  Assert.Equal(PaymentReconciliationReason.ProviderReferenceConflict,h.Store.Values.Single().ReconciliationReason); Assert.Equal(1,h.Provider.Initiations);
 }
 [Fact] public async Task Recording_failure_is_explicit_and_preserves_both_safe_error_contexts() {
  var h=new Harness(); var p=await h.Start(); h.Provider.Status=ProviderPaymentStatus.Succeeded;
  var conflict=new PaymentPersistenceConflictException(PaymentReconciliationReason.ProviderReceiptConflict,new IOException("diagnostic"));
  var failedRecording=new IOException("database unavailable"); h.Unit.Failures.Enqueue(conflict); h.Unit.Failures.Enqueue(failedRecording);
  var error=await Assert.ThrowsAsync<PaymentReconciliationRecordingException>(()=>h.Verify(p.Id));
  var aggregate=Assert.IsType<AggregateException>(error.InnerException); Assert.Contains(conflict,aggregate.InnerExceptions); Assert.Contains(failedRecording,aggregate.InnerExceptions);
  Assert.DoesNotContain(failedRecording.Message,error.SafeMessage); Assert.Empty(h.Outbox.Events);
 }
}
