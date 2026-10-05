using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Identity.Presentation;

[ApiController]
[Route("api/v1/identity")]
public sealed class IdentityController(IIdentityCommands commands, IIdentityQueries queries) : ControllerBase
{
    [AllowAnonymous, HttpPost("register")]
    public async Task<ActionResult<ApiResponse<UserDto>>> Register(RegisterCommand command, CancellationToken ct)
    {
        var user = await commands.RegisterAsync(command, ct);
        return CreatedAtAction(nameof(Me), new ApiResponse<UserDto>(user, HttpContext.TraceIdentifier));
    }

    [AllowAnonymous, HttpPost("login")]
    public async Task<ActionResult<ApiResponse<TokenPair>>> Login(LoginCommand command, CancellationToken ct)
    {
        NoStore();
        return Ok(new ApiResponse<TokenPair>(await commands.LoginAsync(command, ct), HttpContext.TraceIdentifier));
    }

    [AllowAnonymous, HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<TokenPair>>> Refresh(RefreshCommand command, CancellationToken ct)
    {
        NoStore();
        return Ok(new ApiResponse<TokenPair>(await commands.RefreshAsync(command, ct), HttpContext.TraceIdentifier));
    }

    // Possession of the opaque refresh secret authorizes revocation, including after access-token expiry.
    [AllowAnonymous, HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshCommand command, CancellationToken ct)
    {
        NoStore();
        await commands.LogoutAsync(command, ct);
        return NoContent();
    }

    [Authorize, HttpGet("me")]
    public async Task<ActionResult<ApiResponse<UserDto>>> Me(CancellationToken ct)
    {
        NoStore();
        var userId = Guid.Parse(User.FindFirst("sub")!.Value);
        return Ok(new ApiResponse<UserDto>(await queries.GetUserAsync(userId, ct), HttpContext.TraceIdentifier));
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
