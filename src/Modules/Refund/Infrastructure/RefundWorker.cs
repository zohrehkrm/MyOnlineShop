using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.Refund.Application;

namespace MyOnlineShop.Refund.Infrastructure;

public sealed class RefundWorker(IServiceScopeFactory scopes, IOptions<RefundProcessingOptions> options, ILogger<RefundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollingSeconds));
        var unavailableReported = false;
        do
        {
            try
            {
                using var scope = scopes.CreateScope(); var scheduler = scope.ServiceProvider.GetRequiredService<RefundScheduler>();
                if (!scheduler.HasPaymentEvidence)
                {
                    if (!unavailableReported) logger.LogWarning("Refund scheduling blocked: authoritative Phase 8 Payment evidence adapter is not registered. Requests remain pending.");
                    unavailableReported = true;
                }
                else
                {
                    var count = await scheduler.ScheduleAsync(stoppingToken);
                    if (count > 0) logger.LogInformation("Scheduled {RefundCount} durable refund attempts", count);
                    unavailableReported = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (!unavailableReported) logger.LogWarning("Refund scheduling unavailable {FailureKind}; durable requests retained", error.GetType().Name);
                unavailableReported = true;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
