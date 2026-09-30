using TreeEditor.Data;
using TreeEditor.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(Worker.ActivitySourceName));

builder.AddNpgsqlDbContext<TreeDbContext>(TreeDbContext.ConnectionName);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
