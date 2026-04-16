# Base-Image Hypothesis Test: Connection-Establishment Metrics

Testing whether the .NET 10 HTTPS connection-establishment latency regression
([dotnet/runtime#124888](https://github.com/dotnet/runtime/issues/124888)) is caused
by the Debian → Ubuntu switch in default .NET 10 Docker base images.

## Test Matrix

| Label | Runtime | Base Image | OS |
|---|---|---|---|
| net9-debian | .NET 9.0.15 | `aspnet:9.0` | Debian 12 (bookworm) |
| net9-ubuntu | .NET 9.0.15 | `aspnet:9.0-noble` | Ubuntu 24.04 (noble) |
| net9-noble-chiseled | .NET 9.0.15 | `aspnet:9.0-noble-chiseled` | Ubuntu 24.04 (noble chiseled) |
| net10-ubuntu | .NET 10.0.6 | `aspnet:10.0` | Ubuntu 24.04 (noble) |
| net10-noble-chiseled | .NET 10.0.6 | `aspnet:10.0-noble-chiseled` | Ubuntu 24.04 (noble chiseled) |
| net10-azurelinux | .NET 10.0.6 | `aspnet:10.0-azurelinux3.0` | Azure Linux 3.0 |

## Collection Parameters

- **PooledConnectionLifetime**: 30 seconds
- **Collection duration**: 5, 5, 5, 5, 5, 5 minutes (per variant)
- **Cosmos emulator**: `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`
- **Connection mode**: Gateway (required for Linux emulator)

## Results

### Connection Setup (`Experimental.System.Net.Http.Connections`)

| Metric | net9-debian | net9-ubuntu | net9-noble-chiseled | net10-ubuntu | net10-noble-chiseled | net10-azurelinux |
|---|---|---|---|---|---|---|
| count | 118 | 110 | 118 | 118 | 106 | 108 |
| p50 (ms) | 1.7 | 1.9 | 2.1 | 1.9 | 1.9 | 2 |
| p95 (ms) | 2.6 | 3.7 | 3.8 | 3.1 | 3.7 | 3.6 |
| p99 (ms) | 8.7 | 13.6 | 8.5 | 6.7 | 5.4 | 8.2 |
| max (ms) | 10.3 | 14.4 | 9.7 | 8.3 | 6.5 | 9.5 |
| mean (ms) | 1.9 | 2.3 | 2.4 | 2 | 2.2 | 2.2 |
| > 500 ms | 0 | 0 | 0 | 0 | 0 | 0 |

### DNS Lookup (`Experimental.System.Net.NameResolution`)

| Metric | net9-debian | net9-ubuntu | net9-noble-chiseled | net10-ubuntu | net10-noble-chiseled | net10-azurelinux |
|---|---|---|---|---|---|---|
| count | 59 | 55 | 59 | 59 | 53 | 54 |
| p50 (ms) | 0.9 | 1 | 1.1 | 1.1 | 1.1 | 1.1 |
| p95 (ms) | 1.5 | 2.4 | 2.1 | 1.6 | 1.8 | 2.2 |
| p99 (ms) | 3.3 | 7.8 | 3.5 | 2.1 | 2.2 | 2.9 |
| max (ms) | 5.7 | 13.1 | 3.5 | 2.4 | 2.3 | 3.1 |
| mean (ms) | 1 | 1.3 | 1.2 | 1.1 | 1.1 | 1.2 |

### Socket Connect (`Experimental.System.Net.Sockets`)

| Metric | net9-debian | net9-ubuntu | net9-noble-chiseled | net10-ubuntu | net10-noble-chiseled | net10-azurelinux |
|---|---|---|---|---|---|---|
| count | 59 | 55 | 59 | 59 | 53 | 54 |
| p50 (ms) | 0.4 | 0.4 | 0.5 | 0.4 | 0.5 | 0.5 |
| p95 (ms) | 0.8 | 0.9 | 0.9 | 1 | 1.1 | 1.1 |
| p99 (ms) | 1.5 | 1.3 | 1.6 | 1.6 | 1.5 | 2.1 |
| max (ms) | 2 | 1.6 | 2.1 | 2.3 | 1.5 | 2.6 |
| mean (ms) | 0.5 | 0.5 | 0.6 | 0.5 | 0.6 | 0.6 |

### TLS Handshake (`Experimental.System.Net.Security`)

| Metric | net9-debian | net9-ubuntu | net9-noble-chiseled | net10-ubuntu | net10-noble-chiseled | net10-azurelinux |
|---|---|---|---|---|---|---|
| count | 0 | 0 | 0 | 0 | 0 | 0 |
| p50 (ms) | 0 | 0 | 0 | 0 | 0 | 0 |
| p95 (ms) | 0 | 0 | 0 | 0 | 0 | 0 |
| p99 (ms) | 0 | 0 | 0 | 0 | 0 | 0 |
| max (ms) | 0 | 0 | 0 | 0 | 0 | 0 |
| mean (ms) | 0 | 0 | 0 | 0 | 0 | 0 |

### Deltas vs Baseline (net9-debian)

**Connection Setup**

| Metric | net9-ubuntu | net9-noble-chiseled | net10-ubuntu | net10-noble-chiseled | net10-azurelinux |
|---|---|---|---|---|---|
| p50 (ms) | +0.2 ms (+12%) | **+0.4 ms (+24%)** | +0.2 ms (+12%) | +0.2 ms (+12%) | +0.3 ms (+18%) |
| p95 (ms) | **+1.1 ms (+42%)** | **+1.2 ms (+46%)** | +0.5 ms (+19%) | **+1.1 ms (+42%)** | **+1.0 ms (+38%)** |
| p99 (ms) | **+4.9 ms (+56%)** | -0.2 ms (-2%) | **-2.0 ms (-23%)** | **-3.3 ms (-38%)** | -0.5 ms (-6%) |
| mean (ms) | **+0.4 ms (+21%)** | **+0.5 ms (+26%)** | +0.1 ms (+5%) | +0.3 ms (+16%) | +0.3 ms (+16%) |

**DNS Lookup**

| Metric | net9-ubuntu | net9-noble-chiseled | net10-ubuntu | net10-noble-chiseled | net10-azurelinux |
|---|---|---|---|---|---|
| p50 (ms) | +0.1 ms (+11%) | **+0.2 ms (+22%)** | **+0.2 ms (+22%)** | **+0.2 ms (+22%)** | **+0.2 ms (+22%)** |
| p95 (ms) | **+0.9 ms (+60%)** | **+0.6 ms (+40%)** | +0.1 ms (+7%) | **+0.3 ms (+20%)** | **+0.7 ms (+47%)** |
| p99 (ms) | **+4.5 ms (+136%)** | +0.2 ms (+6%) | **-1.2 ms (-36%)** | **-1.1 ms (-33%)** | -0.4 ms (-12%) |
| mean (ms) | **+0.3 ms (+30%)** | +0.2 ms (+20%) | +0.1 ms (+10%) | +0.1 ms (+10%) | +0.2 ms (+20%) |

## Analysis

**The Debian → Ubuntu base-image hypothesis is not supported.** All six variants
produce connection-establishment times within noise of each other (p95 range:
2.6–3.8 ms, mean range: 1.9–2.4 ms). Switching .NET 9 from Debian to Ubuntu
adds ~1 ms at p95 in absolute terms — nowhere near the 500 ms SDK timeout
threshold and not a meaningful contributor to the production regression.

**The regression observed in the original Aspire-based test (on .NET 10.0.3) does
not reproduce on .NET 10.0.6.** The original test showed p95 connection setup
+60% (18.3 → 29.3 ms) and p95 DNS +396% (2.7 → 13.4 ms). In this
containerized test on .NET 10.0.6, the .NET 10 variants are statistically
indistinguishable from .NET 9. This suggests the regression may have been
partially addressed in .NET 10.0.4–10.0.6 patches.

**Important caveats:**
- The Cosmos vnext-preview emulator uses HTTP only (no TLS). The original
  Aspire-based test also had no TLS, so this is an apples-to-apples comparison,
  but production workloads use HTTPS where TLS handshake regression could
  still be a factor.
- Production environments have real network latency, external DNS resolution,
  and TLS negotiation against remote Azure endpoints — conditions that may
  still trigger the SDK's 500 ms first-attempt timeout even if the emulator
  test no longer shows a regression.

## Reproduction

```bash
# Run the full 6-variant matrix (~30 min with 5-min collection per variant)
./scripts/run-matrix.sh 5

# Full 15-min collection per variant (~90 min total)
./scripts/run-matrix.sh

# Generate comparison table from existing results
python scripts/compare.py results/ > RESULTS.md
```

