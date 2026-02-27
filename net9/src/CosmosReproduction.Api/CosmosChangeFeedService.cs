using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace CosmosReproduction.Api;

/// <summary>
/// Background service that listens to the Cosmos container change feed
/// using the SDK's ChangeFeedProcessor. This exercises the same metadata
/// resolution path that triggers the 500ms timeout issue.
/// </summary>
public sealed class CosmosChangeFeedService(
    CosmosClient cosmosClient,
    CosmosContainerInfo containerInfo,
    ILogger<CosmosChangeFeedService> logger) : BackgroundService
{
    private ChangeFeedProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the emulator a moment to become fully available.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        var database = cosmosClient.GetDatabase(containerInfo.DatabaseName);

        // The change feed processor needs a lease container.
        var leaseContainer = await database
            .CreateContainerIfNotExistsAsync("leases", "/id", cancellationToken: stoppingToken);

        logger.LogInformation(
            "Starting change feed processor on {Database}/{Container}",
            containerInfo.DatabaseName,
            containerInfo.ContainerName);

        var feedContainer = cosmosClient.GetContainer(
            containerInfo.DatabaseName,
            containerInfo.ContainerName);

        _processor = feedContainer
            .GetChangeFeedProcessorBuilder<JsonElement>("reproduction-processor", HandleChangesAsync)
            .WithInstanceName(Environment.MachineName)
            .WithLeaseContainer(leaseContainer.Container)
            .WithStartTime(DateTime.MinValue.ToUniversalTime())
            .Build();

        await _processor.StartAsync();

        logger.LogInformation("Change feed processor started");

        // Keep running until shutdown is requested.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private Task HandleChangesAsync(
        ChangeFeedProcessorContext context,
        IReadOnlyCollection<JsonElement> changes,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Change feed received {Count} changes (lease: {LeaseToken})",
            changes.Count,
            context.LeaseToken);

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            logger.LogInformation("Stopping change feed processor");
            await _processor.StopAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
