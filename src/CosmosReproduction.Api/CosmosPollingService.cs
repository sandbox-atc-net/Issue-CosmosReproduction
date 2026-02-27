using System.Diagnostics;
using Microsoft.Azure.Cosmos;

namespace CosmosReproduction.Api;

/// <summary>
/// Background service that continuously calls ReadContainerAsync to trigger
/// metadata/address resolution refreshes. This is the operation most likely
/// to hit the 500ms first-attempt timeout during connection establishment.
/// </summary>
public sealed class CosmosPollingService(
    CosmosClient cosmosClient,
    CosmosContainerInfo containerInfo,
    ILogger<CosmosPollingService> logger) : BackgroundService
{
    /// <summary>
    /// How often to call ReadContainerAsync.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the emulator a moment to become fully available.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        logger.LogInformation(
            "CosmosPollingService started. Polling every {Interval}s against {Database}/{Container}",
            PollingInterval.TotalSeconds,
            containerInfo.DatabaseName,
            containerInfo.ContainerName);

        var container = cosmosClient.GetContainer(
            containerInfo.DatabaseName,
            containerInfo.ContainerName);

        var iteration = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            iteration++;
            var sw = Stopwatch.StartNew();

            try
            {
                // ReadContainerAsync forces metadata/address resolution — this is
                // the control plane request path that uses the 500ms first-attempt timeout.
                var response = await container.ReadContainerAsync(cancellationToken: stoppingToken);

                sw.Stop();

                logger.LogInformation(
                    "Iteration {Iteration}: ReadContainerAsync completed in {ElapsedMs}ms " +
                    "({RequestCharge:F2} RU)",
                    iteration,
                    sw.ElapsedMilliseconds,
                    response.RequestCharge);

                // // Upsert a simple document to exercise the write path.
                // var documentId = $"repro-{iteration % 10}";
                // var partitionKey = new PartitionKey("polling");
                // var document = new
                // {
                //     id = documentId,
                //     pk = "polling",
                //     timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                //     iteration,
                //     runtimeVersion = Environment.Version.ToString(),
                // };
                //
                // var upsertResponse = await container.UpsertItemAsync(
                //     document,
                //     partitionKey,
                //     cancellationToken: stoppingToken);
                //
                // logger.LogInformation(
                //     "Iteration {Iteration}: Upsert completed in {ElapsedMs}ms " +
                //     "({RequestCharge:F2} RU, Status: {StatusCode})",
                //     iteration,
                //     sw.ElapsedMilliseconds,
                //     upsertResponse.RequestCharge,
                //     upsertResponse.StatusCode);
                //
                // // Also perform a point read to exercise the read/metadata path.
                // sw.Restart();
                //
                // var readResponse = await container.ReadItemAsync<dynamic>(
                //     documentId,
                //     partitionKey,
                //     cancellationToken: stoppingToken);
                //
                // sw.Stop();
                //
                // logger.LogInformation(
                //     "Iteration {Iteration}: Point read completed in {ElapsedMs}ms " +
                //     "({RequestCharge:F2} RU, Status: {StatusCode})",
                //     iteration,
                //     sw.ElapsedMilliseconds,
                //     readResponse.RequestCharge,
                //     readResponse.StatusCode);
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                sw.Stop();

                // *** THIS IS THE BUG ***
                // Under .NET 10, connection establishment (TLS handshake) can intermittently
                // exceed the SDK's hard-coded 500ms first-attempt timeout for metadata
                // requests (HttpTimeoutPolicyControlPlaneRetriableHotPath).
                logger.LogError(ex,
                    "Iteration {Iteration}: TIMEOUT after {ElapsedMs}ms — " +
                    "TaskCanceledException during Cosmos operation. " +
                    "This is likely the 500ms metadata timeout. " +
                    "Check Aspire dashboard traces for Experimental.System.Net.* spans " +
                    "to see connection_setup / TLS handshake duration.",
                    iteration,
                    sw.ElapsedMilliseconds);
            }
            catch (CosmosException ex)
            {
                sw.Stop();

                logger.LogError(ex,
                    "Iteration {Iteration}: CosmosException after {ElapsedMs}ms " +
                    "(Status: {StatusCode}, SubStatus: {SubStatusCode})",
                    iteration,
                    sw.ElapsedMilliseconds,
                    ex.StatusCode,
                    ex.SubStatusCode);
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        logger.LogInformation("CosmosPollingService stopped");
    }
}
