using Microsoft.EntityFrameworkCore;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

// Foundation infrastructure only. Business modules own separate contexts and schemas.
public sealed class FoundationDbContext(DbContextOptions<FoundationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("foundation");
    }
}
