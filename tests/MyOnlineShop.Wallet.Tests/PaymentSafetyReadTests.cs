using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Wallet.Contracts;
using Xunit;
namespace MyOnlineShop.Wallet.Tests;
public sealed class PaymentSafetyReadTests
{
 [Fact] public async Task Trusted_safety_reads_return_only_the_owners_primary_currency_and_posted_credit()
 {
  using var h=new Harness(); var safety=h.Services.GetRequiredService<IWalletPaymentSafety>();
  Assert.Null(await safety.GetPrimaryCurrencyAsync(h.Identity.User,default));
  var key=Guid.NewGuid(); var operation=new WalletOperation {IdempotencyKey=key,Amount=10,Currency="USD",ReferenceType="InventoryRefund",ReferenceId="unit",Description="Test"};
  var posted=await h.Operations.CreditAsync(h.Identity.User,h.Identity.User,operation,default);
  Assert.Equal("USD",await safety.GetPrimaryCurrencyAsync(h.Identity.User,default));
  Assert.Equal(posted.Id,(await safety.FindPostedCreditAsync(h.Identity.User,key,default))!.Id);
  Assert.Null(await safety.FindPostedCreditAsync(h.Identity.Other,key,default));
  Assert.Null(await safety.FindPostedCreditAsync(h.Identity.User,Guid.NewGuid(),default));
 }
}
