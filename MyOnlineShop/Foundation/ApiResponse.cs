namespace MyOnlineShop.Foundation;

public sealed record ApiResponse<T>(T Data, string CorrelationId);
public sealed record HealthResponse(string Status);
