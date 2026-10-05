using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Identity.Presentation;

[ApiController]
[Route("api/v1/identity")]
public sealed class IdentityAdministrationController(IIdentityAdministration commands, IIdentityQueries queries) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirst("sub")!.Value);

    [Authorize(Policy = IdentityPermissions.ManageRoles), HttpGet("roles")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleDto>>>> Roles(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<RoleDto>>(await queries.GetRolesAsync(ct), HttpContext.TraceIdentifier));

    [Authorize(Policy = IdentityPermissions.ManageRoles), HttpPost("roles")]
    public async Task<ActionResult<ApiResponse<RoleDto>>> CreateRole(CreateRoleCommand command, CancellationToken ct)
    {
        var role = await commands.CreateRoleAsync(ActorId, command, ct);
        return StatusCode(201, new ApiResponse<RoleDto>(role, HttpContext.TraceIdentifier));
    }

    [Authorize(Policy = IdentityPermissions.ManageUsers), HttpPut("users/{userId:guid}/roles/{roleId:guid}")]
    public async Task<IActionResult> AssignRole(Guid userId, Guid roleId, CancellationToken ct)
    {
        await commands.SetUserRoleAsync(ActorId, userId, roleId, true, ct);
        return NoContent();
    }

    [Authorize(Policy = IdentityPermissions.ManageUsers), HttpDelete("users/{userId:guid}/roles/{roleId:guid}")]
    public async Task<IActionResult> RemoveRole(Guid userId, Guid roleId, CancellationToken ct)
    {
        await commands.SetUserRoleAsync(ActorId, userId, roleId, false, ct);
        return NoContent();
    }

    [Authorize(Policy = IdentityPermissions.ManageRoles), HttpPut("roles/{roleId:guid}/permissions/{permission}")]
    public async Task<IActionResult> AssignPermission(Guid roleId, string permission, CancellationToken ct)
    {
        await commands.SetRolePermissionAsync(ActorId, roleId, permission, true, ct);
        return NoContent();
    }

    [Authorize(Policy = IdentityPermissions.ManageRoles), HttpDelete("roles/{roleId:guid}/permissions/{permission}")]
    public async Task<IActionResult> RemovePermission(Guid roleId, string permission, CancellationToken ct)
    {
        await commands.SetRolePermissionAsync(ActorId, roleId, permission, false, ct);
        return NoContent();
    }

    [Authorize(Policy = IdentityPermissions.ManageUsers), HttpPut("users/{userId:guid}/status")]
    public async Task<IActionResult> Status(Guid userId, ChangeStatusCommand command, CancellationToken ct)
    {
        await commands.SetUserStatusAsync(ActorId, userId, command.IsActive!.Value, ct);
        return NoContent();
    }
}
