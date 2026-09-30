using System.Net;
using System.Text;
using TreeEditor.Contracts;
using TreeEditor.Web.Api;

namespace TreeEditor.Web.Tests.Api;

/// <summary>Every failed request surfaces as an <see cref="HttpRequestException"/>, which is what the UI handles.</summary>
public sealed class TreeApiClientFailureTests
{
    private static readonly Guid Id = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    [Theory]
    [InlineData("list")]
    [InlineData("load")]
    [InlineData("apply")]
    public async Task Response_that_is_not_json_throws_an_http_error(string call)
    {
        // A proxy's error page, with a success status.
        var client = Client(new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html"),
            })));

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(client, call, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("load")]
    [InlineData("apply")]
    public async Task Empty_response_throws_an_http_error(string call)
    {
        var client = Client(new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json"),
            })));

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(client, call, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("load")]
    [InlineData("apply")]
    [InlineData("reset")]
    public async Task Timeout_throws_an_http_error(string call)
    {
        var http = Http(new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The delay only ends by cancellation.");
        }));
        http.Timeout = TimeSpan.FromMilliseconds(50);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Send(new TreeApiClient(http), call, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_by_the_caller_stays_a_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = Client(new StubHandler(async (_, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The delay only ends by cancellation.");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadNodeAsync(Id, cancellation.Token));
    }

    private static TreeApiClient Client(StubHandler handler) => new(Http(handler));

    private static HttpClient Http(StubHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private static Task Send(TreeApiClient client, string call, CancellationToken cancellationToken) => call switch
    {
        "list" => client.ListChildrenAsync(parentId: null, cancellationToken: cancellationToken),
        "load" => client.LoadNodeAsync(Id, cancellationToken),
        "apply" => client.ApplyAsync(new ApplyRequest([], [new NodeEdit(Id, "Gamma", 1001)], []), cancellationToken),
        "reset" => client.ResetAsync(cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call, "Unknown call."),
    };

    /// <summary>Answers every request with what the test's function returns.</summary>
    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
