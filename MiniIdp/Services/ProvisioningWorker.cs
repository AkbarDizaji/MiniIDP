using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiniIdp.Data;
using MiniIdp.Models;

namespace MiniIdp.Services;


public class ProvisioningWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    ILogger<ProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAfterRestartAsync(stoppingToken);

        var pollInterval = TimeSpan.FromSeconds(options.Value.QueuePollIntervalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var foundWork = false;
                try
                {
                    foundWork = await ProvisionNextQueuedServiceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Unexpected error while processing the queue");
                }

                if (!foundWork)
                    await Task.Delay(pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ProvisionNextQueuedServiceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var next = await db.Services
            .Where(s => s.Status == ServiceStatus.Queued)
            .OrderBy(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (next is null) return false;

        logger.LogInformation("Provisioning {Service}", next.Name);
        var provisioner = scope.ServiceProvider.GetRequiredService<ServiceProvisioner>();
        await provisioner.ProvisionAsync(next, ct);
        logger.LogInformation("Finished {Service} with status {Status}", next.Name, next.Status);
        return true;
    }

    private async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ServiceProvisioner>().RecoverAfterRestartAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not reconcile service status with Docker: {Message}", ex.Message);
        }
    }
}
