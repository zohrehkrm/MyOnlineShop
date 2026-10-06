using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Contracts;

namespace MyOnlineShop.Wallet.Presentation;

[ApiController, Authorize, Route("api/v1/wallet")]
public sealed class WalletController(IWalletQueries queries) : ControllerBase
{
    private Guid Owner => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new WalletException("wallet_user", 401, "A valid authenticated user is required.");
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(new ApiResponse<WalletDto>(await queries.GetMyAsync(Owner, ct), HttpContext.TraceIdentifier));
    [HttpGet("transactions")]
    public async Task<IActionResult> Ledger([FromQuery] WalletTransactionsQuery query, CancellationToken ct) =>
        Ok(new ApiResponse<WalletTransactionsPage>(await queries.GetMyTransactionsAsync(Owner, query, ct), HttpContext.TraceIdentifier));
}
[ApiController, Authorize(Policy = IdentityPermissions.CreditWallet), Route("api/v1/admin/wallets")]
public sealed class WalletAdministrationController(IWalletAdministration commands) : ControllerBase
{
    [HttpPost("{userId:guid}/credit")]
    public async Task<IActionResult> Credit(Guid userId, AdminWalletCredit input, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("sub")?.Value, out var actor) || actor == Guid.Empty)
            throw new WalletException("wallet_user", 401, "A valid authenticated user is required.");
        return Ok(new ApiResponse<WalletTransactionDto>(await commands.CreditAsync(actor, userId, input, ct), HttpContext.TraceIdentifier));
    }
}
