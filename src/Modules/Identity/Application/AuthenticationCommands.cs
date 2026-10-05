using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Application;

public sealed class AuthenticationCommands(IIdentityStore store, IIdentityPasswords passwords,
    IIdentityTokens tokens, TimeProvider clock, IRequestContext request) : IIdentityCommands
{
    private sealed class Outcome(TokenPair? tokenPair, IdentityException? error)
    {
        public TokenPair? TokenPair { get; } = tokenPair;
        public IdentityException? Error { get; } = error;
    }

    public async Task<UserDto> RegisterAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        IdentityValidation.Validate(command);
        return await store.InTransactionAsync(async ct =>
        {
            if (await store.FindUserByEmailAsync(User.NormalizeEmail(command.Email), ct) is not null ||
                (command.PhoneNumber is not null && await store.PhoneExistsAsync(command.PhoneNumber, ct)))
                throw new IdentityException("registration_conflict", 409, "Registration could not be completed with the supplied details.");
            var user = User.Create(command.Email, command.FirstName, command.LastName, command.PhoneNumber, clock.GetUtcNow());
            user.SetPasswordHash(passwords.Hash(user, command.Password));
            store.AddUser(user);
            await store.SaveAsync(ct);
            await store.SetUserRoleAsync(user.Id, IdentityPermissions.CustomerRoleId, true, ct);
            Audit(user.Id, user.Id, "Registration", "Succeeded");
            return ToDto(user, [IdentityPermissions.CustomerRole]);
        }, cancellationToken);
    }

    public async Task<TokenPair> LoginAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        IdentityValidation.Validate(command);
        var outcome = await store.InTransactionAsync(async ct =>
        {
            var candidate = await store.FindUserByEmailAsync(User.NormalizeEmail(command.Email), ct);
            var user = candidate is null ? null : await store.LockUserAsync(candidate.Id, ct);
            var now = clock.GetUtcNow();
            var valid = passwords.Verify(user, command.Password, out var needsRehash);
            if (user is null || !user.CanLogin(now) || !valid)
            {
                if (user?.CanLogin(now) == true && !valid)
                    user.RecordFailedLogin(now, tokens.MaximumFailedLogins, tokens.LockoutDuration);
                Audit(null, user?.Id, "Login", "Denied");
                return new Outcome(null, IdentityException.InvalidCredentials());
            }
            if (needsRehash) user.SetPasswordHash(passwords.Hash(user, command.Password));
            user.RecordSuccessfulLogin();
            var session = RefreshSession.Create(user.Id, now, now.Add(tokens.RefreshLifetime));
            var refresh = tokens.CreateRefreshToken();
            store.AddSession(session);
            store.AddRefreshToken(RefreshToken.Create(session.Id, refresh.Hash, now));
            Audit(user.Id, user.Id, "Login", "Succeeded");
            return new Outcome(tokens.Issue(user, session, await store.GetUserRolesAsync(user.Id, ct), refresh), null);
        }, cancellationToken);
        if (outcome.Error is not null) throw outcome.Error;
        return outcome.TokenPair!;
    }

    public async Task<TokenPair> RefreshAsync(RefreshCommand command, CancellationToken cancellationToken)
    {
        IdentityValidation.Validate(command);
        var hash = tokens.HashRefreshToken(command.RefreshToken);
        var outcome = await store.InTransactionAsync(async ct =>
        {
            var reference = await store.FindSessionAsync(hash, ct);
            if (reference is null)
            {
                Audit(null, null, "Refresh", "Denied");
                return new Outcome(null, IdentityException.InvalidRefresh());
            }
            var user = await store.LockUserAsync(reference.UserId, ct);
            var session = await store.GetSessionAsync(reference.SessionId, ct);
            var token = await store.GetRefreshTokenAsync(hash, ct);
            var now = clock.GetUtcNow();
            if (token?.UsedAtUtc is not null)
            {
                session!.Revoke(now, "RefreshReplay");
                Audit(null, reference.UserId, "RefreshReplay", "SessionRevoked", reference.SessionId.ToString());
                return new Outcome(null, IdentityException.InvalidRefresh());
            }
            if (user is null || !user.CanLogin(now) || session?.IsActive(now) != true || token is null)
            {
                Audit(null, reference.UserId, "Refresh", "Denied");
                return new Outcome(null, IdentityException.InvalidRefresh());
            }
            token.Consume(now);
            var refresh = tokens.CreateRefreshToken();
            store.AddRefreshToken(RefreshToken.Create(session.Id, refresh.Hash, now));
            Audit(user.Id, user.Id, "Refresh", "Rotated", session.Id.ToString());
            return new Outcome(tokens.Issue(user, session, await store.GetUserRolesAsync(user.Id, ct), refresh), null);
        }, cancellationToken);
        if (outcome.Error is not null) throw outcome.Error;
        return outcome.TokenPair!;
    }

    public async Task LogoutAsync(RefreshCommand command, CancellationToken cancellationToken)
    {
        IdentityValidation.Validate(command);
        var hash = tokens.HashRefreshToken(command.RefreshToken);
        await store.InTransactionAsync(async ct =>
        {
            var reference = await store.FindSessionAsync(hash, ct);
            if (reference is not null)
            {
                await store.LockUserAsync(reference.UserId, ct);
                var session = await store.GetSessionAsync(reference.SessionId, ct);
                session?.Revoke(clock.GetUtcNow(), "Logout");
                Audit(reference.UserId, reference.UserId, "Logout", "SessionRevoked", reference.SessionId.ToString());
            }
            return true;
        }, cancellationToken);
    }

    private void Audit(Guid? actor, Guid? target, string action, string outcome, string? reference = null) =>
        store.AddAudit(IdentityAudit.Create(actor, target, action, outcome, reference, request.CorrelationId, clock.GetUtcNow()));
    internal static UserDto ToDto(User user, IReadOnlyList<string> roles) => new(user.Id, user.FirstName,
        user.LastName, user.Email, user.PhoneNumber, user.Status == UserStatus.Active, roles);
}
