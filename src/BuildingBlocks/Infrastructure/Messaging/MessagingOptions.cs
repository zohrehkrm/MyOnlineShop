namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public sealed class MessagingOptions
{
    public bool Enabled { get; init; }
    public int PollingSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 20;
    public int MaxAttempts { get; init; } = 8;
    public int RetrySeconds { get; init; } = 5;
    public int MaximumRetrySeconds { get; init; } = 60;
    public int LeaseSeconds { get; init; } = 120;
    public int PublishTimeoutSeconds { get; init; } = 30;
    public int MaximumPayloadBytes { get; init; } = 262144;
    public RabbitMqOptions RabbitMq { get; init; } = new();
    public bool IsValid() => PollingSeconds is >= 1 and <= 60 && BatchSize is >= 1 and <= 100 && MaxAttempts is >= 1 and <= 100 &&
        RetrySeconds is >= 1 and <= 60 && MaximumRetrySeconds >= RetrySeconds && MaximumRetrySeconds <= 300 &&
        PublishTimeoutSeconds is >= 1 and <= 120 && LeaseSeconds > PublishTimeoutSeconds + 10 && LeaseSeconds <= 600 &&
        MaximumPayloadBytes is >= 1024 and <= 1048576 && (!Enabled || RabbitMq.IsValid());
    public TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(MaximumRetrySeconds, RetrySeconds * Math.Pow(2, Math.Min(20, Math.Max(0, attempts - 1)))));
}
public sealed class RabbitMqOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string VirtualHost { get; init; } = "";
    public string Exchange { get; init; } = "";
    public string DeadLetterExchange { get; init; } = "";
    public bool UseTls { get; init; }
    public ushort Prefetch { get; init; } = 10;
    public List<QueueBinding> Queues { get; init; } = [];
    public bool IsValid() => new[] { Host, Username, Password, VirtualHost, Exchange, DeadLetterExchange }.All(value => !string.IsNullOrWhiteSpace(value)) &&
        Port is >= 1 and <= 65535 && Prefetch is >= 1 and <= 100 && Exchange != DeadLetterExchange && Queues.Count > 0 &&
        Queues.All(q => !string.IsNullOrWhiteSpace(q.Queue) && !string.IsNullOrWhiteSpace(q.DeadLetterQueue) && q.Queue != q.DeadLetterQueue && q.RoutingKeys.Count > 0) &&
        Queues.Select(q => q.Queue).Distinct(StringComparer.Ordinal).Count() == Queues.Count &&
        Queues.Where(q => q.ConsumerName is not null).Select(q => q.ConsumerName).Distinct(StringComparer.Ordinal).Count() == Queues.Count(q => q.ConsumerName is not null);
}
public sealed class QueueBinding
{
    public string Queue { get; init; } = "";
    public string DeadLetterQueue { get; init; } = "";
    public string? ConsumerName { get; init; }
    public List<string> RoutingKeys { get; init; } = [];
}
