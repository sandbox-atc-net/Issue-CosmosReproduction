ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

# ── Build stage ─────────────────────────────────────────────────────
FROM ${SDK_IMAGE} AS build
WORKDIR /source

COPY global.json ./
COPY Directory.Build.props ./
COPY src/CosmosReproduction.Api/CosmosReproduction.Api.csproj src/CosmosReproduction.Api/

RUN dotnet restore src/CosmosReproduction.Api/CosmosReproduction.Api.csproj

COPY src/CosmosReproduction.Api/ src/CosmosReproduction.Api/

RUN dotnet publish src/CosmosReproduction.Api/CosmosReproduction.Api.csproj \
    -c Release \
    -o /app \
    --no-restore

# ── Runtime stage ───────────────────────────────────────────────────
FROM ${BASE_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CosmosReproduction.Api.dll"]
