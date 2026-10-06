namespace MyOnlineShop.Refund.Application;

public sealed class RefundProcessingOptions
{
    public bool Enabled { get; set; }
    public int DelayMinutes { get; set; } = 60;
    public int PollingSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 20;
    public int MaximumAttempts { get; set; } = 8;
    public int RetryMinutes { get; set; } = 5;
    public bool IsValid() => DelayMinutes is >= 0 and <= 43200 && PollingSeconds is >= 1 and <= 300 &&
        BatchSize is >= 1 and <= 100 && MaximumAttempts is >= 1 and <= 100 && RetryMinutes is >= 1 and <= 1440;
    public TimeSpan Backoff(int attempt) => TimeSpan.FromMinutes(Math.Min(1440, RetryMinutes * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20))));
}
