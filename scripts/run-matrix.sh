#!/usr/bin/env bash
# run-matrix.sh — Run the Cosmos SDK connection-metrics workload across
# four container variants to test the Debian→Ubuntu base-image hypothesis.
#
# Usage:  ./scripts/run-matrix.sh [DURATION_MINUTES]
#         Default duration is 15 minutes per variant (~60 min total).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DURATION_MINUTES="${1:-15}"
DURATION_SECONDS=$((DURATION_MINUTES * 60))
NETWORK="cosmos-repro-net"
EMULATOR_NAME="cosmos-emulator"
EMULATOR_IMAGE="mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview"
EMULATOR_KEY="C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
RESULTS_DIR="$REPO_ROOT/results"
APP_PORT=8080

# ── Variant matrix ──────────────────────────────────────────────────
# Each entry: LABEL|SDK_IMAGE|BASE_IMAGE|BUILD_CONTEXT
VARIANTS=(
  "net9-debian|mcr.microsoft.com/dotnet/sdk:9.0|mcr.microsoft.com/dotnet/aspnet:9.0|${REPO_ROOT}/net9"
  "net9-ubuntu|mcr.microsoft.com/dotnet/sdk:9.0|mcr.microsoft.com/dotnet/aspnet:9.0-noble|${REPO_ROOT}/net9"
  "net9-noble-chiseled|mcr.microsoft.com/dotnet/sdk:9.0|mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled|${REPO_ROOT}/net9"
  "net10-ubuntu|mcr.microsoft.com/dotnet/sdk:10.0|mcr.microsoft.com/dotnet/aspnet:10.0|${REPO_ROOT}"
  "net10-noble-chiseled|mcr.microsoft.com/dotnet/sdk:10.0|mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled|${REPO_ROOT}"
  "net10-azurelinux|mcr.microsoft.com/dotnet/sdk:10.0|mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0|${REPO_ROOT}"
)

# ── Helpers ─────────────────────────────────────────────────────────
log() { printf "\n=== %s ===\n" "$*"; }

cleanup() {
  log "Cleaning up"
  docker rm -f "$EMULATOR_NAME" 2>/dev/null || true
  for v in "${VARIANTS[@]}"; do
    local label="${v%%|*}"
    docker rm -f "repro-${label}" 2>/dev/null || true
  done
  docker network rm "$NETWORK" 2>/dev/null || true
}

wait_for_emulator() {
  log "Waiting for Cosmos emulator to become ready"
  local max_wait=120
  local elapsed=0
  while [ $elapsed -lt $max_wait ]; do
    # vnext-preview emulator uses HTTP (not HTTPS)
    if curl -s "http://127.0.0.1:8081/" -o /dev/null 2>/dev/null; then
      echo "Emulator is ready (${elapsed}s)"
      return 0
    fi
    sleep 5
    elapsed=$((elapsed + 5))
    echo "  waiting... (${elapsed}s)"
  done
  echo "WARNING: Emulator may not be fully ready after ${max_wait}s — proceeding anyway"
}

wait_for_app() {
  local container_name="$1"
  local max_wait=120
  local elapsed=0
  log "Waiting for $container_name to become healthy"
  while [ $elapsed -lt $max_wait ]; do
    if curl -s "http://127.0.0.1:${APP_PORT}/health" -o /dev/null 2>/dev/null; then
      echo "App is healthy (${elapsed}s)"
      return 0
    fi
    sleep 5
    elapsed=$((elapsed + 5))
    echo "  waiting... (${elapsed}s)"
  done
  echo "WARNING: App may not be fully healthy after ${max_wait}s — proceeding anyway"
}

# ── Main ────────────────────────────────────────────────────────────
trap cleanup EXIT

mkdir -p "$RESULTS_DIR"

# Create docker network
log "Creating Docker network: $NETWORK"
docker network rm "$NETWORK" 2>/dev/null || true
docker network create "$NETWORK"

# Start emulator
log "Starting Cosmos DB emulator"
docker rm -f "$EMULATOR_NAME" 2>/dev/null || true
docker run -d \
  --name "$EMULATOR_NAME" \
  --network "$NETWORK" \
  -p 8081:8081 \
  -e ENABLE_EXPLORER=true \
  "$EMULATOR_IMAGE"

wait_for_emulator

# Run each variant
for variant in "${VARIANTS[@]}"; do
  IFS='|' read -r LABEL SDK_IMAGE BASE_IMAGE BUILD_CONTEXT <<< "$variant"
  CONTAINER_NAME="repro-${LABEL}"
  IMAGE_TAG="cosmos-repro:${LABEL}"

  log "Building $LABEL"
  echo "  SDK:  $SDK_IMAGE"
  echo "  Base: $BASE_IMAGE"
  echo "  Context: $BUILD_CONTEXT"

  docker build \
    -f "$REPO_ROOT/docker/Dockerfile" \
    --build-arg "SDK_IMAGE=${SDK_IMAGE}" \
    --build-arg "BASE_IMAGE=${BASE_IMAGE}" \
    -t "$IMAGE_TAG" \
    "$BUILD_CONTEXT"

  log "Running $LABEL (${DURATION_MINUTES} min)"
  docker rm -f "$CONTAINER_NAME" 2>/dev/null || true
  docker run -d \
    --name "$CONTAINER_NAME" \
    --network "$NETWORK" \
    -p "${APP_PORT}:8080" \
    -e "CosmosOptions__AccountEndpoint=http://${EMULATOR_NAME}:8081" \
    -e "CosmosOptions__AccountKey=${EMULATOR_KEY}" \
    -e "CosmosOptions__DatabaseName=reproduction-db" \
    -e "CosmosOptions__ContainerName=test-items" \
    -e "COSMOS_INIT_DB=true" \
    -e "ASPNETCORE_ENVIRONMENT=Production" \
    -e "Logging__LogLevel__Default=Warning" \
    -e "Logging__LogLevel__CosmosReproduction=Information" \
    "$IMAGE_TAG"

  wait_for_app "$CONTAINER_NAME"

  # Reset metrics to start a clean collection window
  echo "Resetting metrics..."
  curl -s -X POST "http://127.0.0.1:${APP_PORT}/diag/connection-metrics/reset" || true
  echo ""

  # Wait for the configured duration
  echo "Collecting data for ${DURATION_MINUTES} minutes..."
  sleep "$DURATION_SECONDS"

  # Collect results
  log "Collecting metrics for $LABEL"
  RESULT_FILE="$RESULTS_DIR/${LABEL}.json"
  if curl -s "http://127.0.0.1:${APP_PORT}/diag/connection-metrics" > "$RESULT_FILE"; then
    echo "Saved to $RESULT_FILE"
    # Pretty-print a summary
    python "$REPO_ROOT/scripts/compare.py" --single "$RESULT_FILE" 2>/dev/null || \
      python3 "$REPO_ROOT/scripts/compare.py" --single "$RESULT_FILE" 2>/dev/null || \
      cat "$RESULT_FILE"
  else
    echo "ERROR: Failed to collect metrics for $LABEL"
    echo '{}' > "$RESULT_FILE"
  fi

  # Stop the variant container
  docker rm -f "$CONTAINER_NAME" 2>/dev/null || true
  echo ""
done

# Generate comparison report
log "Generating comparison report"
python "$REPO_ROOT/scripts/compare.py" "$RESULTS_DIR" > "$REPO_ROOT/RESULTS.md" 2>/dev/null || \
  python3 "$REPO_ROOT/scripts/compare.py" "$RESULTS_DIR" > "$REPO_ROOT/RESULTS.md" 2>/dev/null || \
  echo "ERROR: Failed to run compare.py — run it manually: python scripts/compare.py results/ > RESULTS.md"

log "Done! Results saved to RESULTS.md"
cat "$REPO_ROOT/RESULTS.md"
