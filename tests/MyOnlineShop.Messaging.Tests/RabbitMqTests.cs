using System.Text.Json;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Refund.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace MyOnlineShop.Messaging.Tests;

public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESSAGING_TEST_RABBITMQ")))
            Skip = "RabbitMQ unavailable. MESSAGING_TEST_RABBITMQ requires a future authorized disposable broker/vhost test run.";
    }
}
public sealed class RabbitMqTests
{
    [RabbitMqFact]
    public async Task Real_broker_confirms_persistent_versioned_message_and_rejects_unroutable_publish()
    {
        var input = JsonSerializer.Deserialize<RabbitMqOptions>(Environment.GetEnvironmentVariable("MESSAGING_TEST_RABBITMQ")!)!;
        var prefix = "MyOnlineShop_MessagingTests_" + Guid.NewGuid().ToString("N");
        var queue = prefix + ".queue"; var deadQueue = prefix + ".dead";
        var rabbit = new RabbitMqOptions { Host = input.Host, Port = input.Port, Username = input.Username, Password = input.Password, VirtualHost = input.VirtualHost,
            UseTls = input.UseTls, Exchange = prefix + ".events", DeadLetterExchange = prefix + ".dlx", Prefetch = 1,
            Queues = [new() { Queue = queue, DeadLetterQueue = deadQueue, RoutingKeys = ["payments.succeeded.v1", "refunds.due.v1"] }] };
        var options = Options.Create(new MessagingOptions { Enabled = true, RabbitMq = rabbit }); var connections = new RabbitMqConnectionFactory(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await connections.ConnectAsync(timeout.Token); await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
        await using var publisher = new RabbitMqPublisher(connections, options);
        try
        {
            var envelope = MessageEnvelope.From(Events.Payment(new Clock(), Guid.NewGuid(), Guid.NewGuid()), "broker-test");
            await publisher.PublishAsync(envelope, timeout.Token);
            var delivery = await channel.BasicGetAsync(queue, autoAck: false, cancellationToken: timeout.Token); Assert.NotNull(delivery);
            Assert.Equal(envelope.MessageId.ToString("D"), delivery.BasicProperties.MessageId); Assert.Equal("application/json", delivery.BasicProperties.ContentType);
            Assert.True(delivery.BasicProperties.Persistent); Assert.Equal(envelope.RoutingKey, delivery.BasicProperties.Type);
            Assert.Equal(envelope.MessageId, JsonSerializer.Deserialize<MessageEnvelope>(delivery.Body.Span)!.MessageId);
            await new RabbitMessageSettlement(channel, delivery.DeliveryTag).AcknowledgeAsync(timeout.Token);
            Assert.Null(await channel.BasicGetAsync(queue, autoAck: false, cancellationToken: timeout.Token));
            // Refund delivery also uses the existing confirmed transport and preserves IDs across duplicates.
            var refund = MessageEnvelope.From(new RefundDueIntegrationEventV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), 1), "refund-broker-test");
            for (var duplicate = 0; duplicate < 2; duplicate++)
            {
                await publisher.PublishAsync(refund, timeout.Token);
                var refundDelivery = await channel.BasicGetAsync(queue, autoAck: false, cancellationToken: timeout.Token);
                Assert.NotNull(refundDelivery); Assert.True(refundDelivery.BasicProperties.Persistent);
                Assert.Equal(refund.MessageId.ToString("D"), refundDelivery.BasicProperties.MessageId);
                Assert.Equal("refunds.due.v1", refundDelivery.RoutingKey);
                Assert.Equal(refund.MessageId, JsonSerializer.Deserialize<MessageEnvelope>(refundDelivery.Body.Span)!.MessageId);
                await new RabbitMessageSettlement(channel, refundDelivery.DeliveryTag).AcknowledgeAsync(timeout.Token);
            }
            await Assert.ThrowsAsync<PublishException>(() => publisher.PublishAsync(envelope with { EventType = "unrouted.event" }, timeout.Token));
        }
        finally
        {
            // Only this test's generated namespace is touched; no existing application queues/exchanges are purged.
            foreach (var name in new[] { queue, deadQueue, rabbit.Exchange, rabbit.DeadLetterExchange })
                if (!name.StartsWith(prefix + ".", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe broker fixture cleanup target.");
            await channel.QueueDeleteAsync(queue, false, false, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(deadQueue, false, false, cancellationToken: CancellationToken.None);
            await channel.ExchangeDeleteAsync(rabbit.Exchange, false, cancellationToken: CancellationToken.None);
            await channel.ExchangeDeleteAsync(rabbit.DeadLetterExchange, false, cancellationToken: CancellationToken.None);
        }
    }
}
