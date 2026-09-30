using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TreeEditor.Contracts;

namespace TreeEditor.AppHost.Tests;

public sealed class SmokeTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    // The sample tree's roots, as the MigrationService seeds them, in listing order.
    private static readonly (Guid Id, string Value)[] SeededRoots =
    [
        (Guid.Parse("0199a000-0000-7000-8000-000000000001"), "Books"),
        (Guid.Parse("0199a000-0000-7000-8000-000000000002"), "Electronics"),
        (Guid.Parse("0199a000-0000-7000-8000-000000000003"), "Garden"),
    ];

    [Fact]
    public async Task App_starts_and_lists_the_seeded_roots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TreeEditor_AppHost>(cancellationToken);
        appHost.Services.AddLogging(logging => logging.AddFilter("Aspire.", LogLevel.Warning));
        appHost.Services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);

        // The MigrationService runs to completion, and only then does the API start and become healthy.
        await app.ResourceNotifications
            .WaitForResourceAsync("migrations", KnownResourceStates.Finished, cancellationToken)
            .WaitAsync(StartupTimeout, cancellationToken);
        await app.ResourceNotifications
            .WaitForResourceHealthyAsync("api", cancellationToken)
            .WaitAsync(StartupTimeout, cancellationToken);

        using var http = app.CreateHttpClient("api");

        var index = await http.GetStringAsync("/", cancellationToken);
        Assert.Contains("_framework/blazor.webassembly", index, StringComparison.Ordinal);

        var roots = await http.GetFromJsonAsync<ChildrenPage>(ApiRoutes.ChildrenOf(null), cancellationToken);
        Assert.NotNull(roots);
        Assert.Equal(SeededRoots, roots.Items.Select(root => (root.Id, root.Value)));
        Assert.False(roots.HasMore);
    }
}
