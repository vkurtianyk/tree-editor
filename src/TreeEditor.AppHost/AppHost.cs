var builder = DistributedApplication.CreateBuilder(args);

// Ephemeral: no data volume, so every run starts from a freshly migrated and seeded database.
var postgres = builder.AddPostgres("postgres").WithImageTag("18.3");
var database = postgres.AddDatabase("treedb");

// Sample tree size: 100000 by default (appsettings.json), 50000 to 1000000.
// Override with `aspire run -- --Parameters:seed-size=1000000` or the Parameters__seed-size environment variable.
var seedSize = builder.AddParameter("seed-size");

// Applies migrations, seeds seed_nodes, fills nodes from it, then exits.
var migrations = builder.AddProject<Projects.TreeEditor_MigrationService>("migrations")
    .WithReference(database)
    .WithEnvironment("Seed__Size", seedSize)
    .WaitFor(database);

// Serves the API and the Web app; starts only after the migrations finished successfully.
builder.AddProject<Projects.TreeEditor_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
