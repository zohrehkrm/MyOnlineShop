using MyOnlineShop.BuildingBlocks.Abstractions;

namespace MyOnlineShop.Catalog.Application;

public sealed class CatalogException(string code, int statusCode, string message) : Exception(message), IApplicationError
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string SafeMessage { get; } = message;
    public static CatalogException NotFound() => new("not_found", 404, "Catalog resource was not found.");
    public static CatalogException Invalid(string message) => new("catalog_validation", 400, message);
    public static CatalogException Conflict() => new("catalog_conflict", 409, "Catalog code, SKU or attribute combination conflicts with an existing record.");
}
