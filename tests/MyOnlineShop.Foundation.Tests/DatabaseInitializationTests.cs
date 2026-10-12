using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyOnlineShop.Foundation;
using MyOnlineShop.Identity.Contracts;
using Xunit;

namespace MyOnlineShop.Foundation.Tests;

public sealed class DatabaseInitializationTests(FoundationFactory factory) : IClassFixture<FoundationFactory>
{
    private sealed class Runner : IDatabaseMigrationRunner
    {
        public List<string> Calls { get; } = [];
        public string? FailAt { get; init; }
        public Task MigrateAsync(DatabaseMigrationStep step, CancellationToken cancellationToken)
        {
            Calls.Add(step.Name);
            if (step.Name == FailAt) throw new InvalidOperationException("private-password-and-connection");
            return Task.CompletedTask;
        }
    }

    private sealed class Bootstrap : IDefaultAdministratorBootstrap
    {
        public bool Enabled { get; init; } = true;
        public bool Invalid { get; init; }
        public int Validations { get; private set; }
        public int Initializations { get; private set; }
        public Task ValidateConfigurationAsync(CancellationToken cancellationToken)
        {
            Validations++;
            if (Invalid) throw new IdentityBootstrapException("IdentityBootstrap:Password is required.");
            return Task.CompletedTask;
        }
        public Task InitializeAsync(CancellationToken cancellationToken) { Initializations++; return Task.CompletedTask; }
    }

    private static DatabaseInitializer Create(Runner runner, Bootstrap bootstrap, bool migrate = true) => new(
        Options.Create(new DatabaseInitializationOptions { MigrateOnStartup = migrate }), runner, bootstrap,
        NullLogger<DatabaseInitializer>.Instance);

    [Fact]
    public async Task Repeated_initialization_uses_the_same_complete_deterministic_plan()
    {
        var runner = new Runner();
        var bootstrap = new Bootstrap();
        var initializer = Create(runner, bootstrap);
        await initializer.InitializeAsync(CancellationToken.None);
        await initializer.InitializeAsync(CancellationToken.None);
        var names = DatabaseMigrationPlan.Steps.Select(step => step.Name).ToArray();
        Assert.Equal(names.Concat(names), runner.Calls);
        Assert.Equal(2, bootstrap.Validations);
        Assert.Equal(2, bootstrap.Initializations);
    }

    [Fact]
    public async Task Migration_failure_reports_context_safely_and_stops_later_steps_and_bootstrap()
    {
        var runner = new Runner { FailAt = "InventoryDbContext" };
        var bootstrap = new Bootstrap();
        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(() =>
            Create(runner, bootstrap).InitializeAsync(CancellationToken.None));
        Assert.Equal("InventoryDbContext", error.Stage);
        Assert.Equal("InvalidOperationException", error.FailureType);
        Assert.DoesNotContain("private-password-and-connection", error.ToString());
        Assert.Equal(new[] { "FoundationDbContext", "IdentityDbContext", "CatalogDbContext", "InventoryDbContext" }, runner.Calls);
        Assert.Equal(0, bootstrap.Initializations);
    }

    [Fact]
    public async Task Missing_bootstrap_configuration_prevents_all_migration_work()
    {
        var runner = new Runner();
        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(() =>
            Create(runner, new Bootstrap { Invalid = true }).InitializeAsync(CancellationToken.None));
        Assert.Equal("Identity bootstrap configuration", error.Stage);
        Assert.Contains("IdentityBootstrap:Password is required", error.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Both_features_disabled_perform_no_database_work()
    {
        var runner = new Runner();
        var bootstrap = new Bootstrap { Enabled = false };
        await Create(runner, bootstrap, migrate: false).InitializeAsync(CancellationToken.None);
        Assert.Empty(runner.Calls);
        Assert.Equal(0, bootstrap.Validations);
        Assert.Equal(0, bootstrap.Initializations);
    }

    [Fact]
    public void Mandatory_initialization_failure_prevents_the_http_host_from_starting()
    {
        var runner = new Runner { FailAt = "FoundationDbContext" };
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Configure<DatabaseInitializationOptions>(options => options.MigrateOnStartup = true);
            services.RemoveAll<IDatabaseMigrationRunner>();
            services.AddScoped<IDatabaseMigrationRunner>(_ => runner);
        }));
        var error = Assert.ThrowsAny<Exception>(() => failing.CreateClient());
        Assert.Contains("FoundationDbContext", error.ToString());
        Assert.DoesNotContain("private-password-and-connection", error.ToString());
        Assert.Equal(new[] { "FoundationDbContext" }, runner.Calls);
    }

    [Fact]
    public async Task Cancellation_prevents_migration_and_bootstrap()
    {
        var runner = new Runner();
        var bootstrap = new Bootstrap { Enabled = false };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(runner, bootstrap).InitializeAsync(cancellation.Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Every_context_has_its_existing_sql_server_migration_assembly_history_and_current_model()
    {
        using var scope = factory.Services.CreateScope();
        Assert.Equal(new[]
        {
            "FoundationDbContext", "IdentityDbContext", "CatalogDbContext", "InventoryDbContext", "CartDbContext",
            "PricingDbContext", "DiscountDbContext", "MessagingDbContext", "OrderDbContext", "WalletDbContext",
            "PaymentDbContext", "RefundDbContext", "ShippingDbContext"
        }, DatabaseMigrationPlan.Steps.Select(step => step.Name));
        Assert.Equal(13, DatabaseMigrationPlan.Steps.Select(step => step.ContextType).Distinct().Count());
        foreach (var step in DatabaseMigrationPlan.Steps)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(step.ContextType);
            Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
            Assert.NotEmpty(context.Database.GetMigrations());
            Assert.False(context.Database.HasPendingModelChanges(), step.Name + " has pending model changes.");
            var migrations = context.GetService<IMigrationsAssembly>();
            Assert.Equal(step.ContextType.Assembly, migrations.Assembly);
            var history = context.GetService<IHistoryRepository>().GetCreateScript();
            Assert.Contains($"[{step.HistorySchema}].[__EFMigrationsHistory]", history);
        }
    }
}
