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
            new { Key = IdentityPermissions.ManageCatalog });
        modelBuilder.Entity<RolePermission>().HasData(
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageUsers },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageRoles },
            new { RoleId = IdentityPermissions.AdministratorRoleId, PermissionKey = IdentityPermissions.ManageCatalog });
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
