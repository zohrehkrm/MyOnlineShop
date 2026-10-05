using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Identity.Tests;

public sealed class IdentitySqlFactAttribute : FactAttribute
{
    public IdentitySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IDENTITY_TEST_SQL_SERVER")))
            Skip = "Database-dependent test deferred. Set IDENTITY_TEST_SQL_SERVER to a Docker SQL Server master connection when integration testing is authorized.";
    }
}

public sealed class IdentityFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = "MyOnlineShop_IdentityTests_" + Guid.NewGuid().ToString("N");
    private readonly string? _masterConnection = Environment.GetEnvironmentVariable("IDENTITY_TEST_SQL_SERVER");
    private bool _created;
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public CapturedLogs Logs { get; } = new();
    public string AdministratorEmail { get; } = "admin-" + Guid.NewGuid().ToString("N") + "@example.test";
    public const string TestPassword = "Test-Only-Password1!";
    public Guid AdministratorId { get; private set; }
    public TokenPair AdministratorTokens { get; private set; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connection = new SqlConnectionStringBuilder(_masterConnection) { InitialCatalog = _databaseName };
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", connection.ConnectionString);
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", SigningKey);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(IdentityProbeController).Assembly));
    }

    public async Task InitializeAsync()
    {
        if (_masterConnection is null) return;
        await using var master = new SqlConnection(_masterConnection);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{_databaseName}]";
        await command.ExecuteNonQueryAsync();
        _created = true;
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        var admin = await scope.ServiceProvider.GetRequiredService<IIdentityAdministration>()
            .BootstrapAdministratorAsync(new RegisterCommand
            {
                FirstName = "Test", LastName = "Administrator", Email = AdministratorEmail, Password = TestPassword
            }, CancellationToken.None);
        AdministratorId = admin.Id;
        AdministratorTokens = await LoginAsync(AdministratorEmail);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (!_created) return;
        // Only the uniquely named database created by this fixture is eligible for cleanup.
        if (!_databaseName.StartsWith("MyOnlineShop_IdentityTests_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(_databaseName["MyOnlineShop_IdentityTests_".Length..], "N", out _))
            throw new InvalidOperationException("Unsafe test database name.");
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_masterConnection);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]";
        await command.ExecuteNonQueryAsync();
    }

    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<(UserDto User, string Email)> RegisterAsync(string? phone = null)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        using var client = Client();
        using var response = await client.PostAsJsonAsync("/api/v1/identity/register", new RegisterCommand
        {
            Email = email, FirstName = "Test", LastName = "Customer", Password = TestPassword, PhoneNumber = phone
        });
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data, email);
    }

    public async Task<TokenPair> LoginAsync(string email)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = TestPassword });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
    }
}

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
    public void Dispose() { }
    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}

[ApiController, Route("identity-probe")]
public sealed class IdentityProbeController : ControllerBase
{
    [Authorize(Roles = IdentityPermissions.AdministratorRole), HttpGet("administrator")]
    public IActionResult Administrator() => Ok(new { Allowed = true });
}
