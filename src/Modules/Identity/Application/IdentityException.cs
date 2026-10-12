using MyOnlineShop.BuildingBlocks.Abstractions;

namespace MyOnlineShop.Identity.Application;

public sealed class IdentityException(string code, int statusCode, string safeMessage,
    IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(safeMessage), IApplicationValidationError
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string SafeMessage { get; } = safeMessage;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
    public static IdentityException InvalidCredentials() => new("invalid_credentials", 401, "Invalid credentials.");
    public static IdentityException InvalidRefresh() => new("invalid_refresh_token", 401, "The refresh token is invalid or expired.");
    public static IdentityException NotFound() => new("not_found", 404, "The requested Identity resource was not found.");
}
