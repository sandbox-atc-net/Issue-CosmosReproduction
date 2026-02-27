namespace CosmosReproduction.Api;

/// <summary>
/// Health check that calls ReadContainerAsync to exercise
/// the Cosmos metadata/address resolution path.
/// </summary>
public sealed class CosmosHealthCheck(
    CosmosClient cosmosClient,
    CosmosContainerInfo containerInfo,
    ILogger<CosmosHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var container = cosmosClient.GetContainer(
                containerInfo.DatabaseName,
                containerInfo.ContainerName);

            var response = await container.ReadContainerAsync(cancellationToken: cancellationToken);

            logger.LogDebug(
                "Cosmos health check succeeded. Request charge: {RequestCharge} RU",
                response.RequestCharge);

            return HealthCheckResult.Healthy(
                $"Container '{containerInfo.ContainerName}' is reachable " +
                $"({response.RequestCharge:F2} RU)");
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex,
                "Cosmos health check failed with status {StatusCode}",
                ex.StatusCode);

            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: $"Cosmos DB unreachable: {ex.Message}",
                exception: ex);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // This is the specific exception we are trying to reproduce.
            // The SDK's HttpTimeoutPolicyControlPlaneRetriableHotPath has a 500ms
            // first-attempt timeout for metadata requests. Under .NET 10, TLS/connection
            // establishment can exceed this threshold intermittently.
            logger.LogError(ex,
                "Cosmos health check timed out (likely the 500ms metadata timeout). " +
                "Exception type: {ExceptionType}",
                ex.GetType().FullName);

            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "Cosmos metadata request timed out (TaskCanceledException) - " +
                             "this is the bug we are reproducing",
                exception: ex);
        }
    }
}
