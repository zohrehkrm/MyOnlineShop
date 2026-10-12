using System.Globalization;
using System.Resources;

namespace MyOnlineShop.BuildingBlocks.Abstractions.Localization;

public static class ErrorMessages
{
    private static readonly ResourceManager Http = new(
        "MyOnlineShop.BuildingBlocks.Abstractions.Localization.HttpErrors", typeof(ErrorMessages).Assembly);
    private static readonly ResourceManager Application = new(
        "MyOnlineShop.BuildingBlocks.Abstractions.Localization.ApplicationErrors", typeof(ErrorMessages).Assembly);

    public static string HttpError(string code) => Http.GetString(code, CultureInfo.CurrentUICulture)
        ?? Http.GetString("request_error", CultureInfo.CurrentUICulture)!;

    public static string ApplicationError(string code, int status) =>
        Application.GetString(code, CultureInfo.CurrentUICulture)
        ?? HttpError(code == "validation_error" ? code : StatusCode(status));

    public static string StatusCode(int status) => status switch
    {
        400 => "bad_request",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        405 => "method_not_allowed",
        409 => "conflict",
        503 => "service_unavailable",
        >= 500 => "internal_error",
        _ => "request_error"
    };
}
