# Cosmos DB SDK 500ms Metadata Timeout Reproduction

Reproduction repo for [dotnet/runtime#124888](https://github.com/dotnet/runtime/issues/124888) and [Azure/azure-cosmos-dotnet-v3#4786](https://github.com/Azure/azure-cosmos-dotnet-v3/issues/4786).

## Problem

After upgrading from .NET 9 to .NET 10, the Azure Cosmos DB SDK (v3.46.0)
intermittently throws `TaskCanceledException` during metadata/address
resolution requests.

### Root cause

The SDK's `HttpTimeoutPolicyControlPlaneRetriableHotPath` hardcodes a
**500ms first-attempt timeout** for control plane requests (metadata
resolution, address refresh, etc.). Under .NET 10, TLS handshake and
connection establishment times appear to have regressed slightly, causing
these operations to intermittently exceed the 500ms budget.

### Symptoms

- `TaskCanceledException` (not `CosmosException`) thrown from operations
  like `ReadContainerAsync`, `UpsertItemAsync`, or any operation that
  triggers a metadata refresh
- The exception originates from `HttpTimeoutHelper` within the SDK
- The issue is intermittent and more likely on cold starts or after
  idle periods when connections have been recycled

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.102 or later)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (9.0.308 or later, for A/B comparison)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for the Cosmos DB Linux emulator)

## Repository structure

```
├── src/                          # .NET 10 solution (primary)
│   ├── CosmosReproduction.AppHost/   # Aspire AppHost (starts emulator + API)
│   └── CosmosReproduction.Api/       # API with polling, change feed, metrics
├── net9/                         # .NET 9 copy (for A/B comparison)
│   ├── src/
│   │   ├── CosmosReproduction.AppHost/
│   │   └── CosmosReproduction.Api/
│   ├── global.json               # Pinned to .NET 9.0.308
│   └── Directory.Build.props     # Targets net9.0
├── global.json                   # Pinned to .NET 10.0.102
└── Directory.Build.props         # Targets net10.0
```

Both solutions contain identical application code. The only differences are
the target framework, SDK version, and Aspire package versions.

## How to reproduce

### Step 1: Start the .NET 10 variant

```bash
cd src/CosmosReproduction.AppHost
dotnet run
```

This launches:
- **Cosmos DB Linux emulator** (Docker container, persistent volume)
- **CosmosReproduction.Api** — background services that continuously poll
  the emulator, triggering connection recycling every 30 seconds
- **Aspire Dashboard** — traces, metrics, and structured logs

The Aspire dashboard URL is printed to the console on startup.

### Step 2: Wait for data collection (10-15 minutes)

The API's `CosmosPollingService` calls `ReadContainerAsync` every 5 seconds.
The `SocketsHttpHandler` is configured with `PooledConnectionLifetime = 30s`,
so new connections (with fresh DNS, socket connect, and TLS handshake) are
established approximately every 30 seconds.

After 15 minutes you'll have ~30 connection establishment samples.

### Step 3: Collect connection metrics

```bash
curl -k https://localhost:7201/diag/connection-metrics
```

This returns a JSON report with percentiles and success/failure counts:

```json
{
  "runtimeVersion": ".NET 10.0.3",
  "collectionDurationMinutes": 15.2,
  "connectionSetup": {
    "count": 30,
    "p50": 4.2,
    "p95": 12.1,
    "p99": 48.3,
    "max": 312.0,
    "mean": 6.8,
    "success": 29,
    "failure": 1,
    "over500ms": 1
  },
  "dnsLookup":     { "count": 30, "p50": 0.1, "p95": 0.3, ... },
  "socketConnect": { "count": 30, "p50": 0.5, "p95": 1.2, ... },
  "tlsHandshake":  { "count": 30, "p50": 3.1, "p95": 8.4, ... }
}
```

Save this output for comparison.

### Step 4: Run the .NET 9 variant

Stop the .NET 10 AppHost (Ctrl+C), then:

```bash
cd net9/src/CosmosReproduction.AppHost
dotnet run
```

The .NET 9 variant reuses the same persistent emulator container.
Wait another 10-15 minutes, then collect metrics:

```bash
curl -k https://localhost:7201/diag/connection-metrics
```

### Step 5: Compare results

The key metrics to compare between .NET 9 and .NET 10:

| Metric                  | .NET 9   | .NET 10  | Delta    |
|-------------------------|----------|----------|----------|
| connection_setup p50    |          |          |          |
| connection_setup p95    |          |          |          |
| connection_setup p99    |          |          |          |
| connection_setup max    |          |          |          |
| tls_handshake p50       |          |          |          |
| tls_handshake p95       |          |          |          |
| tls_handshake p99       |          |          |          |
| tls_handshake max       |          |          |          |
| > 500ms connections     |          |          |          |
| Sample count            |          |          |          |
| Collection duration     |          |          |          |

## Diagnostic endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/diag/connection-metrics` | GET | Connection establishment percentiles, success/failure counts |
| `/diag/connection-metrics/reset` | POST | Reset all counters (start a fresh collection window) |
| `/diag/activity-sources` | GET | Check which experimental activity sources have active listeners |
| `/health` | GET | Overall health check (includes Cosmos DB connectivity) |
| `/` | GET | Runtime version info |

## What the metrics collector tracks

The `ConnectionMetricsCollector` registers an `ActivityListener` for
these experimental System.Net activity sources:

| Activity source | Metric key | What it measures |
|-----------------|------------|------------------|
| `Experimental.System.Net.Http.Connections` | `connectionSetup` | Total connection establishment time |
| `Experimental.System.Net.NameResolution` | `dnsLookup` | DNS resolution duration |
| `Experimental.System.Net.Sockets` | `socketConnect` | TCP socket connect duration |
| `Experimental.System.Net.Security` | `tlsHandshake` | TLS client handshake duration |

For each category the report includes: count, p50, p95, p99, max, mean,
success count, and failure count. The `connectionSetup` category also
includes `over500ms` — the number of connections that exceeded the SDK's
500ms first-attempt timeout threshold.

## Aspire dashboard telemetry

In addition to the `/diag/connection-metrics` endpoint, the Aspire dashboard
provides rich trace visualization. Navigate to **Traces** and look for:

1. **`Experimental.System.Net.Http.Connections`** spans — total connection time
2. **`Experimental.System.Net.Security`** spans — TLS handshake duration
3. **`Experimental.System.Net.Sockets`** spans — raw TCP socket connect time
4. **`Experimental.System.Net.NameResolution`** spans — DNS lookup time

When the sum of these exceeds ~500ms, the SDK's internal
`CancellationTokenSource` fires and the operation fails with
`TaskCanceledException`.

## SDK timeout policy details

The relevant SDK code is in `HttpTimeoutPolicyControlPlaneRetriableHotPath`:

- First attempt: **500ms** timeout
- Retry attempts: 5s, 10s timeouts
- Total maximum: ~65s

The first attempt timeout of 500ms is too aggressive when .NET 10's
connection establishment takes longer than .NET 9's.

## Troubleshooting

### Emulator fails to become healthy

If the Cosmos emulator stays unhealthy with `The response ended prematurely`:

```bash
# Stop the AppHost, then delete the container and volume
docker rm -f <cosmos-container-name>
docker volume rm cosmos-repro-volume

# Restart the AppHost — a fresh container will be created
cd src/CosmosReproduction.AppHost
dotnet run
```

### No TLS handshake data in metrics

If `tlsHandshake` shows zero count, the emulator is running in HTTP mode.
The `connectionSetup`, `dnsLookup`, and `socketConnect` metrics are still
valid for comparison.

### .NET 9 build issues

The `net9/` directory uses Aspire SDK 9.5.2 (vs 13.1.2 for .NET 10).
The AppHost project structure is slightly different — it uses
`Microsoft.NET.Sdk` as the base SDK with `Aspire.AppHost.Sdk` added
as an additional SDK element.

## Environment

- .NET 10.0.3 / .NET 9.0.13
- Microsoft.Azure.Cosmos 3.46.0
- Aspire 13.1.2 (.NET 10) / Aspire 9.5.2 (.NET 9)
- Cosmos DB Linux emulator (`mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`)
- Windows 11 Enterprise (10.0.26200) / Docker Desktop

## Test results

The following results were collected over 15-minute runs on each runtime,
against the same persistent emulator container, on the same machine. **No
traffic shaping or artificial latency was applied** — these are clean
localhost-to-container measurements. As a result, no connections exceeded
the 500ms threshold in this test. In production environments with real
network latency, DNS resolution over the network, and TLS negotiation
against remote endpoints, the regressions shown below would compound with
existing latency and push connection establishment times past the SDK's
500ms first-attempt timeout.

### Connection Setup (`Experimental.System.Net.Http.Connections`)

| Metric | .NET 9 | .NET 10 | Delta |
|---|---|---|---|
| count | 66 | 84 | **+18 (+27%)** |
| p50 | 3.0 ms | 3.6 ms | +0.6 ms (+20%) |
| p95 | 18.3 ms | 29.3 ms | **+11.0 ms (+60%)** |
| p99 | 77.7 ms | 111.5 ms | **+33.8 ms (+43%)** |
| max | 87.9 ms | 127.4 ms | **+39.5 ms (+45%)** |
| mean | 6.4 ms | 9.9 ms | **+3.5 ms (+55%)** |
| > 500 ms | 0 | 0 | — |

### DNS Lookup (`Experimental.System.Net.NameResolution`)

| Metric | .NET 9 | .NET 10 | Delta |
|---|---|---|---|
| count | 33 | 42 | +9 (+27%) |
| p50 | 1.1 ms | 1.5 ms | +0.4 ms (+36%) |
| p95 | 2.7 ms | 13.4 ms | **+10.7 ms (+396%)** |
| p99 | 10.1 ms | 34.6 ms | **+24.5 ms (+243%)** |
| max | 13.0 ms | 39.1 ms | **+26.1 ms (+201%)** |
| mean | 1.6 ms | 3.5 ms | **+1.9 ms (+119%)** |

### Socket Connect (`Experimental.System.Net.Sockets`)

| Metric | .NET 9 | .NET 10 | Delta |
|---|---|---|---|
| count | 33 | 42 | +9 (+27%) |
| p50 | 0.6 ms | 0.7 ms | +0.1 ms |
| p95 | 1.3 ms | 1.6 ms | +0.3 ms |
| mean | 0.7 ms | 0.8 ms | +0.1 ms |

### TLS Handshake (`Experimental.System.Net.Security`)

Both near-zero (emulator running in HTTP mode) — not a factor in this test.

### Key observations

1. **.NET 10 creates 27% more connections** (84 vs 66) in the same 15-minute
   window with identical `PooledConnectionLifetime = 30s`. This suggests
   connections are being recycled more aggressively or more concurrent
   connections are opened.
2. **DNS resolution regressed significantly** — p95 went from 2.7 ms to
   13.4 ms (4x slower), p99 from 10.1 ms to 34.6 ms. This is the largest
   contributor to the connection setup regression.
3. **Connection setup p95/p99 are ~50% slower** on .NET 10. While none
   exceeded 500 ms in this local emulator test, in production with real
   network latency these numbers stack up against the SDK's 500 ms
   first-attempt timeout.
4. **Socket connect is roughly equivalent** between the two runtimes.
