using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Persistence;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", table => table.HasCheckConstraint("CK_Users_Status", "[Status] IN (1, 2)"));
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Email).HasMaxLength(254).IsRequired();
        builder.Property(user => user.NormalizedEmail).HasMaxLength(254).IsRequired();
        builder.HasIndex(user => user.NormalizedEmail).IsUnique();
        builder.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.LastName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.PhoneNumber).HasMaxLength(16);
        builder.HasIndex(user => user.PhoneNumber).IsUnique().HasFilter("[PhoneNumber] IS NOT NULL");
        builder.Property(user => user.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(user => user.RowVersion).IsRowVersion();
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Name).HasMaxLength(64).IsRequired();
        builder.Property(role => role.NormalizedName).HasMaxLength(64).IsRequired();
        builder.HasIndex(role => role.NormalizedName).IsUnique();
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.HasKey(permission => permission.Key);
        builder.Property(permission => permission.Key).HasMaxLength(100);
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.HasKey(link => new { link.UserId, link.RoleId });
        builder.HasOne<User>().WithMany().HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Role>().WithMany().HasForeignKey(link => link.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.HasKey(link => new { link.RoleId, link.PermissionKey });
        builder.Property(link => link.PermissionKey).HasMaxLength(100);
        builder.HasOne<Role>().WithMany().HasForeignKey(link => link.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Permission>().WithMany().HasForeignKey(link => link.PermissionKey).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        builder.HasKey(session => session.Id);
        builder.Property(session => session.RevocationReason).HasMaxLength(64);
        builder.HasIndex(session => new { session.UserId, session.RevokedAtUtc });
        builder.HasIndex(session => session.ExpiresAtUtc);
        builder.HasOne<User>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(token => token.Id);
        builder.Property(token => token.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasOne<RefreshSession>().WithMany().HasForeignKey(token => token.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuditConfiguration : IEntityTypeConfiguration<IdentityAudit>
{
    public void Configure(EntityTypeBuilder<IdentityAudit> builder)
    {
        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Action).HasMaxLength(64).IsRequired();
        builder.Property(audit => audit.Outcome).HasMaxLength(64).IsRequired();
        builder.Property(audit => audit.TargetReference).HasMaxLength(150);
        builder.Property(audit => audit.CorrelationId).HasMaxLength(64).IsRequired();
        builder.HasIndex(audit => new { audit.TargetUserId, audit.OccurredAtUtc });
        builder.HasIndex(audit => audit.OccurredAtUtc);
        // Historical actor/target IDs are retained without deletion-coupled foreign keys.
    }
}
