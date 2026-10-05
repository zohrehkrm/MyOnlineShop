namespace MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int MaxRetryCount { get; init; } = 3;
}
