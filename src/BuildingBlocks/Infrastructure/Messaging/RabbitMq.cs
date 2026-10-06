using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public interface IRabbitMqConnectionFactory { Task<IConnection> ConnectAsync(CancellationToken ct); }
public sealed class RabbitMqConnectionFactory(IOptions<MessagingOptions> options) : IRabbitMqConnectionFactory
{
    public Task<IConnection> ConnectAsync(CancellationToken ct)
    {
        var settings = options.Value; var rabbit = settings.RabbitMq;
        if (!rabbit.IsValid()) throw new InvalidOperationException("RabbitMQ configuration is invalid.");
        var factory = new ConnectionFactory { HostName = rabbit.Host, Port = rabbit.Port, UserName = rabbit.Username, Password = rabbit.Password,
            VirtualHost = rabbit.VirtualHost, AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(settings.PublishTimeoutSeconds),
            Ssl = new SslOption { Enabled = rabbit.UseTls, ServerName = rabbit.Host } };
        return factory.CreateConnectionAsync(ct);
    }
}
public static class RabbitMqTopology
{
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions settings, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(settings.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        foreach (var binding in settings.Queues)
        {
            await channel.QueueDeclareAsync(binding.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
            await channel.QueueBindAsync(binding.DeadLetterQueue, settings.DeadLetterExchange, binding.DeadLetterQueue, cancellationToken: ct);
            await channel.QueueDeclareAsync(binding.Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-dead-letter-exchange"] = settings.DeadLetterExchange,
                    ["x-dead-letter-routing-key"] = binding.DeadLetterQueue }, cancellationToken: ct);
            foreach (var key in binding.RoutingKeys) await channel.QueueBindAsync(binding.Queue, settings.Exchange, key, cancellationToken: ct);
        }
    }
}
public sealed class RabbitMqPublisher(IRabbitMqConnectionFactory connections, IOptions<MessagingOptions> options) : IMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate(); await _gate.WaitAsync(ct);
        try
        {
            if (_connection?.IsOpen != true || _channel?.IsOpen != true)
            {
                await ResetAsync(); _connection = await connections.ConnectAsync(ct);
                _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
                await RabbitMqTopology.DeclareAsync(_channel, options.Value.RabbitMq, ct);
            }
            var properties = new BasicProperties { Persistent = true, MessageId = envelope.MessageId.ToString("D"), ContentType = "application/json",
                Type = envelope.RoutingKey, CorrelationId = envelope.CorrelationId };
            // Confirmation tracking makes this await fail on nack/mandatory-return as well as connection failure.
            await _channel!.BasicPublishAsync(options.Value.RabbitMq.Exchange, envelope.RoutingKey, mandatory: true, basicProperties: properties,
                body: JsonSerializer.SerializeToUtf8Bytes(envelope), cancellationToken: ct);
        }
        catch { await ResetAsync(); throw; }
        finally { _gate.Release(); }
    }
    private async Task ResetAsync()
    {
        if (_channel is not null) { try { await _channel.DisposeAsync(); } catch { } _channel = null; }
        if (_connection is not null) { try { await _connection.DisposeAsync(); } catch { } _connection = null; }
    }
    public async ValueTask DisposeAsync() { await _gate.WaitAsync(); try { await ResetAsync(); } finally { _gate.Release(); _gate.Dispose(); } }
}
public sealed class MessageRequestContext : IRequestContext
{
    public string CorrelationId { get; set; } = "";
}
public sealed class RabbitMessageSettlement(IChannel channel, ulong deliveryTag) : IMessageSettlement
{
    public Task AcknowledgeAsync(CancellationToken ct) => channel.BasicAckAsync(deliveryTag, false, ct).AsTask();
    public Task RejectAsync(bool requeue, CancellationToken ct) => channel.BasicNackAsync(deliveryTag, false, requeue, ct).AsTask();
}
public sealed class RabbitMqConsumerWorker(IServiceScopeFactory scopes, IRabbitMqConnectionFactory connections,
    IOptions<MessagingOptions> options, ILogger<RabbitMqConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var unavailable = false;
        using (var validation = scopes.CreateScope())
        {
            var consumers = validation.ServiceProvider.GetServices<IMessageConsumer>().ToArray();
            foreach (var binding in options.Value.RabbitMq.Queues.Where(value => value.ConsumerName is not null))
            {
                var consumer = consumers.Single(value => value.ConsumerName == binding.ConsumerName);
                if (!binding.RoutingKeys.Contains($"{consumer.EventType}.v{consumer.Version}", StringComparer.Ordinal))
                    throw new InvalidOperationException("Consumer queue must bind its registered versioned event type.");
            }
        }
        using var retryTimer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RetrySeconds));
        do
        {
            try
            {
                await using var connection = await connections.ConnectAsync(stoppingToken);
                var channels = new List<IChannel>();
                try
                {
                    foreach (var binding in options.Value.RabbitMq.Queues.Where(value => value.ConsumerName is not null))
                    {
                        using (var scope = scopes.CreateScope())
                            _ = scope.ServiceProvider.GetServices<IMessageConsumer>().Single(value => value.ConsumerName == binding.ConsumerName);
                        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken); channels.Add(channel);
                        await RabbitMqTopology.DeclareAsync(channel, options.Value.RabbitMq, stoppingToken);
                        await channel.BasicQosAsync(0, options.Value.RabbitMq.Prefetch, false, stoppingToken);
                        var receiver = new AsyncEventingBasicConsumer(channel);
                        receiver.ReceivedAsync += async (_, delivery) =>
                        {
                            try
                            {
                                if (delivery.Body.Length > options.Value.MaximumPayloadBytes) throw new InvalidMessageException();
                                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(delivery.Body.Span) ?? throw new InvalidMessageException();
                                envelope.Validate();
                                if (delivery.BasicProperties.MessageId != envelope.MessageId.ToString("D") || delivery.BasicProperties.Type != envelope.RoutingKey)
                                    throw new InvalidMessageException();
                                using var scope = scopes.CreateScope();
                                scope.ServiceProvider.GetRequiredService<MessageRequestContext>().CorrelationId = envelope.CorrelationId;
                                var consumer = scope.ServiceProvider.GetServices<IMessageConsumer>().Single(value => value.ConsumerName == binding.ConsumerName);
                                var dispatcher = new ConsumerDeliveryDispatcher(scope.ServiceProvider.GetRequiredService<IMessageProcessor>(), options.Value);
                                await dispatcher.DeliverAsync(consumer, envelope, new RabbitMessageSettlement(channel, delivery.DeliveryTag), stoppingToken);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                            catch (Exception error)
                            {
                                var requeue = error is not InvalidMessageException and not JsonException;
                                logger.LogWarning("Message delivery failed {ConsumerName} {FailureKind}", binding.ConsumerName, requeue ? "Infrastructure" : "InvalidEnvelope");
                                if (requeue)
                                {
                                    // Short throttle only. Durable retry eligibility/count remain in Inbox; RabbitMQ retains unacked delivery.
                                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RetrySeconds));
                                    await timer.WaitForNextTickAsync(stoppingToken);
                                }
                                await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue, stoppingToken);
                            }
                        };
                        await channel.BasicConsumeAsync(binding.Queue, autoAck: false, consumer: receiver, cancellationToken: stoppingToken);
                    }
                    using var healthTimer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollingSeconds));
                    if (unavailable) logger.LogInformation("RabbitMQ consumer connection recovered."); unavailable = false;
                    while (connection.IsOpen && channels.All(channel => channel.IsOpen) && await healthTimer.WaitForNextTickAsync(stoppingToken)) { }
                }
                finally { foreach (var channel in channels) await channel.DisposeAsync(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { if (!unavailable) logger.LogWarning("RabbitMQ consumer connection unavailable; durable deliveries remain recoverable."); unavailable = true; }
        } while (await retryTimer.WaitForNextTickAsync(stoppingToken));
    }
}
