namespace MyOnlineShop.BuildingBlocks.Abstractions;

public sealed record ApiResponse<T>(T Data, string CorrelationId);
