# Docker Base-Image Matrix Test

Tests the hypothesis from [dotnet/runtime#124888](https://github.com/dotnet/runtime/issues/124888)
that the .NET 10 connection-establishment latency regression is caused by the
Debian → Ubuntu switch in default Docker base images.

## Background

.NET 9 defaults to Debian bookworm base images; .NET 10 dropped Debian entirely
and defaults to Ubuntu noble. `.NET 10 has no Debian aspnet image at all` — the
tag `aspnet:10.0-bookworm-slim` does not exist on MCR. The closest non-Ubuntu
alternative is `aspnet:10.0-azurelinux3.0`.

## Test Matrix

| Label | .NET | Base Image | OS |
|---|---|---|---|
| net9-debian | 9.0 | `aspnet:9.0` | Debian 12 (bookworm) |
| net9-ubuntu | 9.0 | `aspnet:9.0-noble` | Ubuntu 24.04 (noble) |
| net9-noble-chiseled | 9.0 | `aspnet:9.0-noble-chiseled` | Ubuntu 24.04 (noble chiseled) |
| net10-ubuntu | 10.0 | `aspnet:10.0` | Ubuntu 24.04 (noble) |
| net10-noble-chiseled | 10.0 | `aspnet:10.0-noble-chiseled` | Ubuntu 24.04 (noble chiseled) |
| net10-azurelinux | 10.0 | `aspnet:10.0-azurelinux3.0` | Azure Linux 3.0 |

The `noble-chiseled` variants are included because Azure Container Apps
defaults to `<ContainerFamily>noble-chiseled</ContainerFamily>` — this is
the image many production workloads actually run on.

## How to Run

```bash
# Full matrix — 15 min per variant, ~60 min total
./scripts/run-matrix.sh

# Quick sanity check — 5 min per variant, ~20 min total
./scripts/run-matrix.sh 5
```

The script:
1. Creates a Docker network and starts the Cosmos DB emulator (vnext-preview)
2. Builds and runs each variant sequentially against the same emulator
3. Resets metrics, waits the configured duration, then collects `/diag/connection-metrics`
4. Saves each result to `results/<label>.json`
5. Generates `RESULTS.md` with a comparison table

## Prerequisites

- Docker Desktop with Linux containers
- ~8 GB RAM (emulator uses ~2 GB)
- Python 3 (for `compare.py` report generation)

## Regenerating the Report

If you already have result files in `results/`:

```bash
python scripts/compare.py results/ > RESULTS.md
```

## Troubleshooting

### Emulator won't start

The vnext-preview emulator needs port 8081 free. Check for a leftover container:

```bash
docker rm -f cosmos-emulator
```

### App can't connect to emulator

If networking between containers is fussy, edit `run-matrix.sh` and change
`EMULATOR_NAME` references in the `CosmosOptions__AccountEndpoint` env var to
`host.docker.internal`, then run the emulator with `-p 8081:8081` (which it
already has). The comparison still holds since all variants use the same path.

### Build fails for net9 variant

The `net9/` directory has its own `global.json` (pinned to 9.0.308) and
`Directory.Build.props` (targeting `net9.0` with `LangVersion=13.0`). The
Dockerfile uses `net9/` as the build context for .NET 9 variants, so these
files are used automatically.
