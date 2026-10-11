using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Contracts;
namespace MyOnlineShop.Payment.Presentation;
[ApiController,Authorize,Route("api/v1/payments")]
public sealed class PaymentController(IPaymentCommands commands,IPaymentQueries queries) : ControllerBase {
 private Guid Owner => Guid.TryParse(User.FindFirst("sub")?.Value,out var id) && id != Guid.Empty ? id : throw new PaymentException("payment_user",401,"A valid authenticated user is required.");
 [HttpPost]
 public async Task<IActionResult> Initiate(InitiatePaymentInput input,CancellationToken ct) {
  Response.Headers.CacheControl = "no-store";
  var result = await commands.InitiateAsync(Owner,input,ct);
  return CreatedAtAction(nameof(Get),new {id=result.Id},new ApiResponse<PaymentDto>(result,HttpContext.TraceIdentifier));
 }
 [HttpGet("{id:guid}")]
 public async Task<IActionResult> Get(Guid id,CancellationToken ct) { Response.Headers.CacheControl = "no-store"; return Ok(new ApiResponse<PaymentDto>(await queries.GetMyAsync(Owner,id,ct),HttpContext.TraceIdentifier)); }
 [HttpPost("{id:guid}/verify")]
 public async Task<IActionResult> Verify(Guid id,VerifyPaymentInput input,CancellationToken ct) { Response.Headers.CacheControl = "no-store"; return Ok(new ApiResponse<PaymentDto>(await commands.VerifyAsync(Owner,id,input.ProviderReference,ct),HttpContext.TraceIdentifier)); }
 [AllowAnonymous,HttpPost("{id:guid}/callback")]
 public async Task<IActionResult> Callback(Guid id,VerifyPaymentInput input,CancellationToken ct) {
  Response.Headers.CacheControl = "no-store";
  // An untrusted callback only requests server verification. It receives no owner/payment details.
  await commands.VerifyAsync(null,id,input.ProviderReference,ct);
  return NoContent();
 }
}

