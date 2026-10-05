# Phase 1 foundation

The existing `MyOnlineShop` project is the ASP.NET Core API host. `src/BuildingBlocks/Abstractions` contains shared technical contracts; `src/BuildingBlocks/Infrastructure` provides SQL Server persistence registration. Business modules are introduced only in their requested phases and own separate DbContexts, schemas and migration histories. `FoundationDbContext` is intentionally empty and must not become a shared business context.

## Run

Requires the .NET 10 SDK. From the repository root:

```powershell
dotnet restore MyOnlineShop.slnx
dotnet run --project MyOnlineShop --launch-profile https
```

Development uses SQL Server LocalDB with integrated authentication. Production requires `ConnectionStrings__SqlServer` from environment variables or a secret provider; no production credentials are checked in. `Database__CommandTimeoutSeconds` (1–300) and `Database__MaxRetryCount` (0–10) override validated database settings.

Development documentation: `/swagger/index.html` and `/openapi/v1.json`. Both are disabled outside Development. `/api/v1/health` reports process liveness only and does not promise database readiness.

## API contracts and logging

Successful JSON responses use `ApiResponse<T>` with `data` and `correlationId`. Errors use Problem Details with `status`, `title`, `type`, `code` and `correlationId`; validation failures include field-keyed `errors`. Empty HTTP errors and MVC client errors follow the same model.

`X-Correlation-ID` accepts a single value of 1–64 ASCII letters, digits, dots, underscores or hyphens. Missing, multiple or invalid values are replaced with a generated ID. The ID is returned in the response header, exposed through scoped `IRequestContext` and included in logging scopes. It is diagnostic metadata, not authentication or an idempotency key.

Logs use the built-in JSON console provider with UTC timestamps and structured fields. Request bodies, query strings, credentials and exception messages are not logged by the foundation. Unexpected errors return safe messages in every environment.

## Migrations

```powershell
dotnet tool restore
dotnet ef migrations script --idempotent --project src/BuildingBlocks/Infrastructure --startup-project MyOnlineShop --context FoundationDbContext -- --environment Development
dotnet ef migrations has-pending-model-changes --project src/BuildingBlocks/Infrastructure --startup-project MyOnlineShop --context FoundationDbContext -- --environment Development
```

`InitialFoundation` is an empty baseline with a generated model snapshot. Its SQL script creates only the `foundation` schema and migration history table. Review generated SQL before applying it through a controlled deployment. API startup does not apply migrations, create databases or reset data. EF tools use the same host configuration and DI registration as runtime.

## Verification

```powershell
dotnet build MyOnlineShop/MyOnlineShop.csproj
dotnet test tests/MyOnlineShop.Foundation.Tests/MyOnlineShop.Foundation.Tests.csproj
```

HTTP integration tests exercise the actual pipeline, SQL Server provider registration and offline migration-script generation. Test-only controllers are registered by the test host, not shipped as application endpoints.

The optional SQL connectivity test reads `FOUNDATION_TEST_SQL_SERVER` and executes `SELECT 1`; it makes no schema or data changes. Use an existing test database or LocalDB `master`. Without this variable, that test is explicitly skipped.

Compatible security fixes are pinned for `Microsoft.OpenApi` and the SQL client's transitive `System.Security.Cryptography.Xml` dependency after NuGet audit warnings. Other existing package versions remain unchanged.

References: [ASP.NET Core API error handling](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0), [EF Core design-time context creation](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation), [OpenAPI security advisory](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc), [XML dependency security advisory](https://github.com/advisories/GHSA-23rf-6693-g89p).
