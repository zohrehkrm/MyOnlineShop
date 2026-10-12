using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Infrastructure.Persistence;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using MyOnlineShop.Payment.Infrastructure.Persistence;
using MyOnlineShop.Refund.Infrastructure.Persistence;
using MyOnlineShop.Shipping.Infrastructure.Persistence;

namespace MyOnlineShop.Foundation;

public sealed class DatabaseInitializationOptions
{
    public bool MigrateOnStartup { get; set; }
}

public sealed record DatabaseMigrationStep(Type ContextType, string HistorySchema)
{
    public string Name => ContextType.Name;
}

public static class DatabaseMigrationPlan
{
    // Composition-root ordering; each module retains its own context, assembly and history.
    public static IReadOnlyList<DatabaseMigrationStep> Steps { get; } = Array.AsReadOnly(new[]
    {
        new DatabaseMigrationStep(typeof(FoundationDbContext), "foundation"),
        new DatabaseMigrationStep(typeof(IdentityDbContext), "identity"),
        new DatabaseMigrationStep(typeof(CatalogDbContext), "catalog"),
        new DatabaseMigrationStep(typeof(InventoryDbContext), "inventory"),
        new DatabaseMigrationStep(typeof(CartDbContext), "cart"),
        new DatabaseMigrationStep(typeof(PricingDbContext), "pricing"),
        new DatabaseMigrationStep(typeof(DiscountDbContext), "discount"),
        new DatabaseMigrationStep(typeof(MessagingDbContext), "messaging"),
        new DatabaseMigrationStep(typeof(OrderDbContext), "ordering"),
        new DatabaseMigrationStep(typeof(WalletDbContext), "wallet"),
        new DatabaseMigrationStep(typeof(PaymentDbContext), "payment"),
        new DatabaseMigrationStep(typeof(RefundDbContext), "refund"),
        new DatabaseMigrationStep(typeof(ShippingDbContext), "shipping"),
    });
}

public interface IDatabaseMigrationRunner
{
    Task MigrateAsync(DatabaseMigrationStep step, CancellationToken cancellationToken);
}

public sealed class EfDatabaseMigrationRunner(IServiceScopeFactory scopes) : IDatabaseMigrationRunner
{
    public async Task MigrateAsync(DatabaseMigrationStep step, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(step.ContextType);
        // SQL Server EF Core 10 acquires the provider migration lock and applies only pending migrations.
        await context.Database.MigrateAsync(cancellationToken);
    }
}

public sealed class DatabaseInitializationException : Exception
{
    public DatabaseInitializationException(string stage, Exception failure)
        : base($"Database initialization failed at {stage} ({failure.GetType().Name}). " +
            (failure is IdentityBootstrapException ? failure.Message + " " : string.Empty) +
            "Later initialization did not run.")
    {
        Stage = stage;
        FailureType = failure.GetType().Name;
        Failure = failure;
    }

    public string Stage { get; }
    public string FailureType { get; }
    // Preserve the cause for controlled diagnostics without including secret-bearing exception text in ToString().
    internal Exception Failure { get; }
}

public sealed class DatabaseInitializer(
    IOptions<DatabaseInitializationOptions> options, IDatabaseMigrationRunner migrations,
    IDefaultAdministratorBootstrap bootstrap, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (bootstrap.Enabled)
        {
            // Fail missing/invalid credentials before any database work.
            await RunStepAsync("Identity bootstrap configuration", bootstrap.ValidateConfigurationAsync, cancellationToken);
        }
        if (options.Value.MigrateOnStartup)
        {
            foreach (var step in DatabaseMigrationPlan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogInformation("Applying pending migrations for {DbContext}", step.Name);
                await RunStepAsync(step.Name, ct => migrations.MigrateAsync(step, ct), cancellationToken);
                logger.LogInformation("Migrations completed for {DbContext}", step.Name);
            }
        }
        if (bootstrap.Enabled)
            await RunStepAsync("Default administrator bootstrap", bootstrap.InitializeAsync, cancellationToken);
    }

    private async Task RunStepAsync(string stage, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        try { await action(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            // Do not attach/log the raw exception: provider messages can include connection details.
            logger.LogCritical("Database initialization failed at {Stage} with {FailureType}", stage, failure.GetType().Name);
            throw new DatabaseInitializationException(stage, failure);
        }
    }
}
