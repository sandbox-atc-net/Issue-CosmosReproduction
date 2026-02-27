// Enable the experimental Azure SDK activity source.
// This must be set before any Azure SDK client is created.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", isEnabled: true);

// Suppress the noisy Cosmos SDK "DocDBTrace" output (304 Not Modified responses
// from ReadContainerAsync are expected and not errors).
System.Diagnostics.Trace.Listeners.Clear();

var builder = WebApplication.CreateBuilder(args);

// ── Cosmos configuration ────────────────────────────────────────────
var cosmosOptions = builder.Configuration.GetSection("CosmosOptions");
var accountEndpoint = cosmosOptions["AccountEndpoint"] ?? throw new InvalidOperationException("CosmosOptions:AccountEndpoint is required");
var accountKey = cosmosOptions["AccountKey"] ?? throw new InvalidOperationException("CosmosOptions:AccountKey is required");
var databaseName = cosmosOptions["DatabaseName"] ?? throw new InvalidOperationException("CosmosOptions:DatabaseName is required");
var containerName = cosmosOptions["ContainerName"] ?? "test-items";

builder.Services.AddSingleton<CosmosClient>(_ =>
{
    var clientOptions = new CosmosClientOptions
    {
        // Gateway mode is required for the Linux emulator.
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,

        // Accept the emulator's self-signed certificate.
        // Use SocketsHttpHandler (not HttpClientHandler) so the experimental
        // System.Net activity sources emit connection_setup / TLS / socket traces.
        HttpClientFactory = () =>
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
                // Force connection recycling so experimental System.Net traces
                // (connection_setup, DNS, socket connect) are emitted periodically,
                // not just at startup.
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            };
            return new HttpClient(handler);
        },

        // Enable distributed tracing so Cosmos operations appear in the Aspire dashboard.
        CosmosClientTelemetryOptions = new CosmosClientTelemetryOptions
        {
            DisableDistributedTracing = false,
            CosmosThresholdOptions =
            {
                // Set latency thresholds matching the production configuration.
                PointOperationLatencyThreshold = TimeSpan.FromMilliseconds(200),
                NonPointOperationLatencyThreshold = TimeSpan.FromMilliseconds(500)
            }
        },
    };

    return new CosmosClient(accountEndpoint, accountKey, clientOptions);
});

// Store database/container names for injection.
builder.Services.AddSingleton(new CosmosContainerInfo(databaseName, containerName));

// ── Health checks ───────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<CosmosHealthCheck>("cosmosdb", tags: ["dependency"])
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"]);

// ── Background services ─────────────────────────────────────────────
builder.Services.AddSingleton<ConnectionMetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionMetricsCollector>());
builder.Services.AddHostedService<CosmosPollingService>();
builder.Services.AddHostedService<CosmosChangeFeedService>();

// ── OpenTelemetry ───────────────────────────────────────────────────
const string serviceName = "CosmosReproduction.Api";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName))
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("System.Net.Http")
            .AddMeter("System.Net.NameResolution")
            .AddMeter("System.Net.Security");
    })
    .WithTracing(tracing =>
    {
        tracing.SetSampler(new AlwaysOnSampler());
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("System.Net.Http")
            .AddSource("Experimental.System.Net.Http.Connections")
            .AddSource("Experimental.System.Net.NameResolution")
            .AddSource("Experimental.System.Net.Security")
            .AddSource("Experimental.System.Net.Sockets")
            .AddSource("Azure.Cosmos.Operation");
    });

// OTLP exporter: connects to Aspire dashboard automatically when
// OTEL_EXPORTER_OTLP_ENDPOINT is set by the AppHost.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

// ── Build and run ───────────────────────────────────────────────────
var app = builder.Build();

app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => true,
});

app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});

app.MapGet("/", () => Results.Ok(new
{
    Status = "Running",
    DotNetVersion = Environment.Version.ToString(),
    RuntimeFramework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
}));

// Diagnostic endpoint: connection establishment metrics (percentiles, success/failure).
app.MapGet("/diag/connection-metrics", (ConnectionMetricsCollector collector) =>
    Results.Ok(collector.GetReport()));

app.MapPost("/diag/connection-metrics/reset", (ConnectionMetricsCollector collector) =>
{
    collector.Reset();
    return Results.Ok(new { status = "reset" });
});

// Diagnostic endpoint: check if experimental activity sources have listeners.
app.MapGet("/diag/activity-sources", () =>
{
    var sourceNames = new[]
    {
        "System.Net.Http",
        "Experimental.System.Net.Http.Connections",
        "Experimental.System.Net.NameResolution",
        "Experimental.System.Net.Security",
        "Experimental.System.Net.Sockets",
        "Azure.Cosmos.Operation",
    };

    var results = sourceNames.Select(name =>
    {
        var src = new ActivitySource(name);
        var hasListeners = src.HasListeners();
        src.Dispose();
        return new { Name = name, HasListeners = hasListeners };
    });

    return Results.Ok(results);
});

await app.RunAsync();

// ── Supporting record ───────────────────────────────────────────────
public record CosmosContainerInfo(string DatabaseName, string ContainerName);
