namespace MyOnlineShop.Identity.Contracts;

// Startup-only port. It is deliberately not exposed by any controller.
public interface IDefaultAdministratorBootstrap
{
    bool Enabled { get; }
    Task ValidateConfigurationAsync(CancellationToken cancellationToken);
    Task InitializeAsync(CancellationToken cancellationToken);
}

// Startup failures with deliberately safe text, never provider/credential exception messages.
public sealed class IdentityBootstrapException(string message) : InvalidOperationException(message);
