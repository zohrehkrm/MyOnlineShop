using System.Data.Common;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

// Infrastructure-only enlistment for module-owned contexts in the same SQL database.
// Business contracts do not expose EF or SQL transaction objects.
public interface ILocalSqlTransactionParticipant
{
    string Name { get; }
    Task EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct);
    Task DetachAsync(CancellationToken ct);
}
