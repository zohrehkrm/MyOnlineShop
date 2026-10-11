using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Tests;
using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Payment.Infrastructure.Persistence;
using Xunit;
namespace MyOnlineShop.Payment.Tests;
public sealed class PaymentApiAndModelTests {
 [Fact] public async Task Owner_endpoints_require_existing_jwt_authentication() {
  using var factory=new IdentityHttpFactory(); using var client=factory.Client();
  Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/v1/payments",new InitiatePaymentInput())).StatusCode);
  Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/payments/"+Guid.NewGuid())).StatusCode);
 }
 [Fact] public async Task Simulated_provider_http_verification_uses_real_identity_but_not_client_status() {
  using var factory=new IdentityHttpFactory(); using var registration=factory.Client();
  const string password="Payment-Http-Test1!";
  var created=await registration.PostAsJsonAsync("/api/v1/identity/register",new RegisterCommand {FirstName="Payment",LastName="Test",Email="payment@example.test",Password=password});
  Assert.Equal(HttpStatusCode.Created,created.StatusCode);
  var user=(await created.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data;
  var login=await registration.PostAsJsonAsync("/api/v1/identity/login",new LoginCommand{Email="payment@example.test",Password=password});
  var token=(await login.Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
  var h=new Harness(user.Id);
  using var app=factory.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<IPaymentCommands>(h.Commands)));
  using var client=app.CreateClient(new(){BaseAddress=new("https://localhost"),AllowAutoRedirect=false});
  client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token.AccessToken);
  var init=await client.PostAsJsonAsync("/api/v1/payments",new InitiatePaymentInput{OrderId=h.Order,IdempotencyKey=h.Key});
  Assert.Equal(HttpStatusCode.Created,init.StatusCode); Assert.True(init.Headers.CacheControl!.NoStore);
  var p=(await init.Content.ReadFromJsonAsync<ApiResponse<PaymentDto>>())!.Data;
  var callback=await client.PostAsJsonAsync($"/api/v1/payments/{p.Id}/callback",new{ProviderReference=h.Provider.Reference,Status="Succeeded",Amount=1});
  Assert.Equal(HttpStatusCode.NoContent,callback.StatusCode); Assert.Empty(h.Outbox.Events);
  h.Provider.Status=ProviderPaymentStatus.Succeeded;
  var verify=await client.PostAsJsonAsync($"/api/v1/payments/{p.Id}/verify",new VerifyPaymentInput{ProviderReference=h.Provider.Reference});
  Assert.Equal(HttpStatusCode.OK,verify.StatusCode); Assert.Single(h.Outbox.Events);
  var clientReversal=await client.PostAsJsonAsync($"/api/v1/payments/{p.Id}/callback",new {ProviderReference=h.Provider.Reference,Status="Chargeback",Amount=1});
  Assert.Equal(HttpStatusCode.NoContent,clientReversal.StatusCode); Assert.False(h.Store.Values.Single().ReconciliationRequired);
  Assert.Single(h.Outbox.Events); // Only the authenticated simulated backend result is evidence.
  Assert.Equal("AwaitingPayment",h.Orders.Status);
 }
 [Fact] public void Offline_sql_model_and_migration_have_owned_payment_keys_and_receipt_uniqueness() {
  using var db=new PaymentDbContext(new DbContextOptionsBuilder<PaymentDbContext>()
   .UseSqlServer("Server=localhost;Database=PaymentOffline;Integrated Security=True").Options);
  var entity=db.Model.FindEntityType(typeof(MyOnlineShop.Payment.Domain.PaymentRecord))!;
  Assert.Equal("payment",entity.GetSchema()); Assert.Contains(entity.GetIndexes(),x=>x.IsUnique && x.Properties.Any(p=>p.Name=="ReceiptId"));
  Assert.Contains(entity.GetIndexes(),x=>x.IsUnique && x.Properties.Count==1 && x.Properties[0].Name=="OrderId");
  Assert.Equal(4,entity.FindProperty("Amount")!.GetScale()); Assert.True(entity.FindProperty("RowVersion")!.IsConcurrencyToken);
  Assert.False(db.Database.HasPendingModelChanges());
  var sql=db.GetService<IMigrator>().GenerateScript();
  Assert.Contains("CREATE TABLE [payment].[Payments]",sql); Assert.Contains("CREATE TABLE [payment].[Allocations]",sql);
  Assert.Contains("CK_Payment_Verified",sql); Assert.Contains("ReceiptId",sql);
 }
}

