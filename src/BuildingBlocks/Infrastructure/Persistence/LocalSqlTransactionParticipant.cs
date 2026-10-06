using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

public sealed class LocalSqlTransactionParticipant<TContext>(TContext context, string name) : ILocalSqlTransactionParticipant where TContext : DbContext
{
    private string? _originalConnectionString;
    public string Name => name;
    public async Task EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("Participant already enlisted.");
        _originalConnectionString = context.Database.GetConnectionString(); context.ChangeTracker.Clear();
        context.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await context.Database.UseTransactionAsync(transaction, ct);
    }
    public async Task DetachAsync(CancellationToken ct)
    {
        await context.Database.UseTransactionAsync(null, ct); context.Database.SetDbConnection(null);
        context.Database.SetConnectionString(_originalConnectionString); _originalConnectionString = null; context.ChangeTracker.Clear();
    }
}
