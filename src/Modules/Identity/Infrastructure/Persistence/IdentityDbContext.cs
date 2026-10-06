using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshSession> Sessions => Set<RefreshSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdentityAudit> Audit => Set<IdentityAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identity");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
        modelBuilder.Entity<Role>().HasData(
            new { Id = IdentityPermissions.AdministratorRoleId, Name = IdentityPermissions.AdministratorRole, NormalizedName = "ADMINISTRATOR" },
            new { Id = IdentityPermissions.CustomerRoleId, Name = IdentityPermissions.CustomerRole, NormalizedName = "CUSTOMER" });
        modelBuilder.Entity<Permission>().HasData(
            new { Key = IdentityPermissions.ManageUsers }, new { Key = IdentityPermissions.ManageRoles },
            new { Key = IdentityPermissions.ManageCatalog }, new { Key = IdentityPermissions.ViewInventory },
            new { Key = IdentityPermissions.ReceiveInventory }, new { Key = IdentityPermissions.AdjustInventory },
            new { Key = IdentityPermissions.DeductInventory }, new { Key = IdentityPermissions.ManagePricing }, new { Key = IdentityPermissions.ManageDiscount },
            new { Key = IdentityPermissions.ViewOrders }, new { Key = IdentityPermissions.ManageOrders }, new { Key = IdentityPermissions.CreditWallet },
            new { Key = IdentityPermissions.ManageShippingMethods }, new { Key = IdentityPermissions.ManageShipments }, new { Key = IdentityPermissions.ViewShipments });
        modelBuilder.Entity<RolePermission>().HasData(
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageUsers },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageRoles },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageCatalog },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ViewInventory },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ReceiveInventory },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.AdjustInventory },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.DeductInventory },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManagePricing },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageDiscount },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ViewOrders },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageOrders },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.CreditWallet },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageShippingMethods },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageShipments },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ViewShipments });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAudit()
    {
        if (ChangeTracker.Entries<IdentityAudit>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Identity audit records are append-only.");
    }
}
