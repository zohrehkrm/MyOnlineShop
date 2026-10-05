using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Domain;

namespace MyOnlineShop.Discount.Application;

public sealed class DiscountException(string code, int status, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => status;
    public string SafeMessage => Message;
    public static DiscountException Invalid(string message = "Discount input is invalid.") => new("discount_validation", 400, message);
    public static DiscountException NotFound() => new("discount_not_found", 404, "Discount not found.");
    public static DiscountException Conflict() => new("discount_conflict", 409, "Discount changed. Reload and retry.");
}
public interface IDiscountStore
{
    Task<DiscountRule?> GetAsync(Guid id, CancellationToken ct);
    void Add(DiscountRule rule);
}
public interface IDiscountUnitOfWork { Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct); }
public sealed class DiscountCommands(IDiscountStore store, IDiscountUnitOfWork unit, ICatalogVariantReferences variants,
    ICatalogQueries catalog, TimeProvider clock) : IDiscountCommands
{
    public Task<DiscountRuleDto> CreateAsync(DiscountInput input, Guid actorId, CancellationToken ct) => SaveAsync(null, input, actorId, ct);
    public Task<DiscountRuleDto> UpdateAsync(Guid id, DiscountInput input, Guid actorId, CancellationToken ct) => SaveAsync(id, input, actorId, ct);
    private async Task<DiscountRuleDto> SaveAsync(Guid? id, DiscountInput input, Guid actorId, CancellationToken ct)
    {
        return await unit.ExecuteAsync(async token =>
        {
            var rule = id is null ? DiscountRule.Create(input, actorId, clock.GetUtcNow()) : await store.GetAsync(id.Value, token) ?? throw DiscountException.NotFound();
            rule.Update(input, actorId, clock.GetUtcNow());
            if (input.ProductVariantId is { } variantId && await variants.GetAsync(variantId, token) is null) throw DiscountException.Invalid("Catalog variant does not exist.");
            if (input.ProductId is { } productId) await catalog.GetProductAsync(productId, true, token);
            if (input.CategoryId is { } categoryId) await catalog.GetCategoryAsync(categoryId, true, token);
            if (id is null) store.Add(rule);
            return rule.Dto();
        }, ct);
    }
    public async Task<DiscountRuleDto> SetActiveAsync(Guid id, bool active, Guid actorId, CancellationToken ct)
    {
        return await unit.ExecuteAsync(async token =>
        {
            var rule = await store.GetAsync(id, token) ?? throw DiscountException.NotFound();
            rule.SetActive(active, actorId, clock.GetUtcNow()); return rule.Dto();
        }, ct);
    }
}
