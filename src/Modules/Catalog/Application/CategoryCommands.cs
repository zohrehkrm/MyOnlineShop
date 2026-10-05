using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Application;

public sealed class CategoryCommands(ICategoryRepository categories, ICatalogUnitOfWork unitOfWork)
{
    public async Task<CategoryDto> SaveAsync(Guid? id, CategoryInput input, CancellationToken ct)
    {
        CatalogValidation.Validate(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            await unitOfWork.LockCategoryHierarchyAsync(token);
            var existing = id is null ? null : await categories.GetAsync(id.Value, token) ?? throw CatalogException.NotFound();
            var category = existing ?? Category.Create(input.Name, input.Code, input.ParentId, input.IsActive);
            if (await categories.CodeExistsAsync(CatalogRules.Code(input.Code), id, token)) throw CatalogException.Conflict();
            var ancestors = new List<Guid>();
            var parentId = input.ParentId;
            while (parentId is not null)
            {
                if (parentId == category.Id || ancestors.Contains(parentId.Value))
                    throw CatalogException.Invalid("Category hierarchy cannot contain a cycle.");
                var parent = await categories.GetAsync(parentId.Value, token)
                    ?? throw CatalogException.Invalid("Parent category does not exist.");
                if (input.IsActive && !parent.IsActive) throw CatalogException.Invalid("An active category requires active ancestors.");
                ancestors.Add(parent.Id); parentId = parent.ParentId;
            }
            Category.ValidateParentChain(category.Id, ancestors);
            if (!input.IsActive && await categories.HasActiveChildrenAsync(category.Id, token))
                throw CatalogException.Invalid("Deactivate active child categories before deactivating their parent.");
            category.Update(input.Name, input.Code, input.ParentId, input.IsActive);
            if (existing is null) categories.Add(category);
            return CatalogMapping.Dto(category);
        }, ct);
    }
}
