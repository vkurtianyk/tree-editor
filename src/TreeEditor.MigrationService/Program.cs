using TreeEditor.Data;
using TreeEditor.MigrationService;
using TreeEditor.MigrationService.Seeding;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(Worker.ActivitySourceName));

builder.AddNpgsqlDbContext<TreeDbContext>(TreeDbContext.ConnectionName);
builder.Services.AddOptions<SeedOptions>()
    .BindConfiguration(SeedOptions.SectionName)
    .Validate(
        options => options.Size is >= SampleTree.MinSize and <= SampleTree.MaxSize,
        $"Seed:Size must be between {SampleTree.MinSize} and {SampleTree.MaxSize}.")
    .ValidateOnStart();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
