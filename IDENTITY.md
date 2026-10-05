# Phase 2 Identity

Identity uses its own Domain, Contracts, Application, Infrastructure and Presentation projects under `src/Modules/Identity`. Application command handlers own state changes; query handlers return DTOs. Its repository abstraction stays inside Identity. Foundation configuration, SQL Server provider, exception pipeline, model validation, JSON logging and correlation context are reused.

The success envelope now lives in BuildingBlocks Abstractions so module controllers can reuse it without depending on the API host. Foundation error handling accepts a shared safe application-error contract; no separate error pipeline was introduced.

## Configuration

Set `IdentitySecurity__SigningKeyBase64` through environment variables or a secret provider. It must encode at least 32 cryptographically random bytes. There is no checked-in key or development fallback. For a temporary local process, generate a key without printing it:

```powershell
$env:IdentitySecurity__SigningKeyBase64 = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project MyOnlineShop --launch-profile https
```

Changing the key invalidates existing access tokens. Keep the configured key stable across instances and restarts. Production SQL credentials continue to use `ConnectionStrings__SqlServer` as described in FOUNDATION.md.

Other `IdentitySecurity` settings: issuer, audience, access lifetime (default 10 minutes, permitted 1–30), absolute session lifetime (default 30 days, permitted 1–30), maximum failed logins (default 5), and lockout duration (default 15 minutes). Configuration is validated at startup.

## Registration and authentication

Required registration fields are FirstName, LastName, Email and Password. PhoneNumber is optional and, if supplied, uses international E.164-style format (`+` followed by 8–15 digits, with a nonzero leading country digit). Email is normalized with trimming and invariant uppercase and has a unique index. PhoneNumber has a filtered unique index; multiple users may omit it. Additional profile fields can be added independently of authentication later.

Passwords are 12–128 characters and require uppercase and lowercase letters, a digit and a non-whitespace symbol. ASP.NET Core's versioned PasswordHasher uses 600,000 iterations and supports rehashing older hashes on successful login. Plaintext passwords are never persisted. Unknown-user verification performs a dummy password-hash check. Wrong passwords, unknown users, inactive users and locked users return the same invalid-credentials message.

Public registration creates an active Customer; email/phone verification and OTP infrastructure are deferred. Client-supplied roles or activation fields cannot grant privileges. Five failed logins lock the account for the configured interval.

JWT validation checks signature, HS256 algorithm, issuer, audience, expiration, required subject/session/security-stamp/token-ID claims, and issuance time. A 30-second clock tolerance applies. Each authenticated request also checks the persisted active user and session and reloads roles and permissions. This intentionally adds SQL reads to provide immediate logout, deactivation and permission revocation. Tokens contain identifiers and role names, not personal profile data.

Refresh tokens are random 256-bit opaque values. Only SHA-256 token hashes are stored. Successful refresh consumes the old token and adds a replacement in the same transaction without extending the absolute session expiration. Reuse of any consumed token revokes its entire session, including outstanding access tokens. Concurrent rotation is serialized through a user-row update lock. If a refresh response is lost, retrying the consumed token revokes the session; the client must log in again.

Logout is idempotent and authorized by possession of the refresh secret, so it remains usable after access-token expiration. Tokens are supplied in request bodies or bearer headers, never URL query strings. Authentication/profile responses use `Cache-Control: no-store`. Clients must protect refresh tokens in secure storage; this API does not issue browser cookies.

## Endpoints

All routes start with `/api/v1/identity`.

| Method and route | Authorization | Result |
|---|---|---|
| POST `/register` | Anonymous | 201, UserDto |
| POST `/login` | Anonymous | 200, token pair |
| POST `/refresh` | Refresh secret | 200, rotated token pair |
| POST `/logout` | Refresh secret | 204 |
| GET `/me` | Authenticated | 200, UserDto |
| GET `/roles` | `identity.roles.manage` | 200, role DTOs |
| POST `/roles` | `identity.roles.manage` | 201, role DTO |
| PUT/DELETE `/users/{userId}/roles/{roleId}` | `identity.users.manage` | 204 |
| PUT/DELETE `/roles/{roleId}/permissions/{permission}` | `identity.roles.manage` | 204 |
| PUT `/users/{userId}/status` | `identity.users.manage` | 204; explicit `IsActive` required |

The permission catalog initially contains `identity.users.manage` and `identity.roles.manage`. New permissions are added through reviewed code/migrations as modules need them. Built-in roles are Customer and Administrator. Administrator has both Identity permissions; Customer has neither. ASP.NET role authorization and the Administrator role policy are available alongside permission requirements/handlers. Role assignments invalidate the user's existing access tokens; a valid refresh session can obtain tokens with the new assignments.

Business conflicts return 409, validation errors 400, invalid credentials/tokens 401 and denied permissions 403. All errors use Foundation Problem Details and correlation IDs. EF entities, password hashes and stored token hashes are never API response DTOs.

## Initial administrator

No user credentials are seeded. After reviewing and applying the Identity migration through your deployment process, supply FirstName, LastName, Email and Password through `IdentityBootstrap__...` environment variables or a secret provider, then explicitly run:

```powershell
dotnet run --project MyOnlineShop -- --BootstrapIdentityAdmin=true
```

This command provisions the first administrator and exits. It refuses to run if any Administrator assignment already exists, and uses a database application lock to serialize bootstrap attempts. Remove bootstrap credentials/configuration afterward. Normal API startup does not provision users or apply migrations. There is no HTTP bootstrap endpoint.

## Persistence, audit and migration

`IdentityDbContext` owns the `identity` schema and its migration history. InitialIdentity creates users, roles, permissions, join tables, sessions, refresh-token hashes and audit records. It seeds only role/permission metadata. Unique indexes enforce normalized email, optional phone, normalized role names and token hashes. Relationship foreign keys restrict deletion, and User uses SQL Server rowversion.

Command transactions include business changes and append-only audit records. Login failure and refresh-replay outcomes commit their security audit/state before returning an error. Audits record safe action/outcome metadata, actor and target IDs, UTC time and correlation ID. They exclude passwords, tokens, email addresses and phone numbers. Audit updates/deletes are rejected by normal synchronous and asynchronous SaveChanges paths; database administrators remain responsible for access control and retention.

The reviewed idempotent SQL is in `artifacts/identity-migration.sql`. No application migration has been applied. To regenerate or verify:

```powershell
dotnet ef migrations script --idempotent --project src/Modules/Identity/Infrastructure --startup-project MyOnlineShop --context IdentityDbContext --no-build --output artifacts/identity-migration.sql -- --environment Development
dotnet ef migrations has-pending-model-changes --project src/Modules/Identity/Infrastructure --startup-project MyOnlineShop --context IdentityDbContext --no-build -- --environment Development
```

## Tests and current verification limit

```powershell
dotnet test tests/MyOnlineShop.Identity.Tests/MyOnlineShop.Identity.Tests.csproj
```

Without `IDENTITY_TEST_SQL_SERVER`, tests cover application rules and the actual HTTP authentication/authorization pipeline using an explicit in-memory repository test double. Offline checks inspect the actual SQL Server EF model and generated migration. These do not validate SQL transaction behavior or database constraints at runtime.

The separate SQL suite requires an existing SQL Server master connection in `IDENTITY_TEST_SQL_SERVER`, with permission to create a temporary database. Its fixture creates a uniquely named `MyOnlineShop_IdentityTests_<guid>` database, applies only Identity migrations there, and deletes only that database on completion. It never resets or migrates the application database. This suite verifies SQL persistence, audit, constraints and concurrent refresh rotation.

The SQL suite was not executed because its LocalDB command was declined. Phase 2 remains pending that verification. No OTP, SMS, RabbitMQ, Redis or later business modules were implemented.

References: [ASP.NET Core JWT validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0), [ASP.NET Core password hashing configuration](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0).
