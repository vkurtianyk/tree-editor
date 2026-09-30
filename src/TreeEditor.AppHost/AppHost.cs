var builder = DistributedApplication.CreateBuilder(args);

// Ephemeral: no data volume, so every run starts from a freshly migrated and seeded database.
var postgres = builder.AddPostgres("postgres").WithImageTag("18.3");
var database = postgres.AddDatabase("treedb");

// Applies migrations, seeds seed_nodes, fills nodes from it, then exits.
var migrations = builder.AddProject<Projects.TreeEditor_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

// Serves the API and the Web app; starts only after the migrations finished successfully.
builder.AddProject<Projects.TreeEditor_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
