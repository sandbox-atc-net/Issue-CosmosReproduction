#pragma warning disable ASPIRECOSMOSDB001
var builder = DistributedApplication.CreateBuilder(args);

// The well-known Cosmos DB emulator account key (public, non-secret).
const string emulatorAccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

var cosmos = builder
    .AddAzureCosmosDB("cosmos")
    .RunAsPreviewEmulator(emulator =>
    {
        emulator
            .WithDataExplorer()
            .WithDataVolume("cosmos-repro-volume")
            .WithLifetime(ContainerLifetime.Persistent);
    });

var database = cosmos.AddCosmosDatabase("reproduction-db");
database.AddContainer("test-items", "/pk");
database.AddContainer("leases", "/id");

var api = builder
    .AddProject<Projects.CosmosReproduction_Api>("api")
    .WithEnvironment("CosmosOptions__AccountEndpoint", cosmos.GetEndpoint("emulator"))
    .WithEnvironment("CosmosOptions__AccountKey", emulatorAccountKey)
    .WithEnvironment("CosmosOptions__DatabaseName", "reproduction-db")
    .WithEnvironment("CosmosOptions__ContainerName", "test-items")
    .WaitFor(cosmos)
    .WaitFor(database);

await builder.Build().RunAsync();