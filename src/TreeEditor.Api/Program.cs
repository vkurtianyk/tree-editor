using Scalar.AspNetCore;
using TreeEditor.Api.Apply;
using TreeEditor.Api.Errors;
using TreeEditor.Api.Nodes;
using TreeEditor.Api.Reset;
using TreeEditor.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// No EF retrying strategy: it rejects the explicit transactions that later writes (apply, reset) use.
builder.AddNpgsqlDbContext<TreeDbContext>(TreeDbContext.ConnectionName, settings => settings.DisableRetry = true);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
// Throw binding failures in every environment so BadRequestExceptionHandler can describe them.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOpenApi();

var app = builder.Build();

// Every error, thrown or returned without a body, becomes RFC 9457 ProblemDetails.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("Tree editor API"));
app.MapDefaultEndpoints();

app.MapNodeEndpoints();
app.MapApplyEndpoints();
app.MapResetEndpoints();

// Unknown API routes are a 404 ProblemDetails, not the Web app's index.html.
app.MapFallback("/api/{**path}", () => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found",
        detail: "No API endpoint matches this route."))
    .ExcludeFromDescription();

// The Web app: its static assets, and index.html for client-side routes.
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

await app.RunAsync();
