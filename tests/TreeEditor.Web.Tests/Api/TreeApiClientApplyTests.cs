using System.Net;
using System.Net.Http.Json;
using System.Text;
using TreeEditor.Contracts;
using TreeEditor.Web.Api;

namespace TreeEditor.Web.Tests.Api;

public sealed class TreeApiClientApplyTests
{
    private static readonly Guid Id = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    [Fact]
    public async Task Posts_the_request_and_reads_the_response()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"nodes":[{"id":"0199a000-0000-7000-8000-000000000001","value":"Gamma (1)","version":5000,"isDeleted":false}]}
            """, "application/json");
        var client = Client(handler);
        var request = new ApplyRequest([], [new NodeEdit(Id, "Gamma", 1001)], []);

        var response = await client.ApplyAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(new AppliedNode(Id, "Gamma (1)", 5000, IsDeleted: false), Assert.Single(response.Nodes));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(ApiRoutes.Apply, handler.Path);
        var sent = await JsonContent.Create(request).ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sent, handler.Body);
    }

    [Fact]
    public async Task Conflict_problem_is_thrown_with_its_conflicts()
    {
        var client = Client(new StubHandler(HttpStatusCode.Conflict, """
            {"title":"The changes conflict with the database; nothing was applied.","status":409,
             "detail":"1 element(s) changed in the database after they were loaded.",
             "conflicts":[{"id":"0199a000-0000-7000-8000-000000000001","reason":"VersionChanged","value":"Other","version":2002,"isDeleted":false}]}
            """));

        var error = await Assert.ThrowsAsync<ApplyRejectedException>(() => client.ApplyAsync(
            new ApplyRequest([], [new NodeEdit(Id, "Gamma", 1001)], []), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(
            "The changes conflict with the database; nothing was applied. " +
            "1 element(s) changed in the database after they were loaded.",
            error.Message);
        Assert.Equal(
            new NodeConflict(Id, ConflictReason.VersionChanged, "Other", 2002, IsDeleted: false),
            Assert.Single(error.Conflicts));
    }

    [Fact]
    public async Task Validation_problem_is_thrown_with_its_errors_in_the_message()
    {
        var client = Client(new StubHandler(HttpStatusCode.BadRequest, """
            {"title":"The changes are invalid; nothing was applied.","status":400,
             "errors":{"edits[0].value":["A value is required."]}}
            """));

        var error = await Assert.ThrowsAsync<ApplyRejectedException>(() => client.ApplyAsync(
            new ApplyRequest([], [new NodeEdit(Id, " ", 1001)], []), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("The changes are invalid; nothing was applied. A value is required.", error.Message);
        Assert.Empty(error.Conflicts);
    }

    [Fact]
    public async Task Other_failures_throw_an_http_error()
    {
        var client = Client(new StubHandler(HttpStatusCode.InternalServerError, "", "text/plain"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ApplyAsync(
            new ApplyRequest([], [new NodeEdit(Id, "Gamma", 1001)], []), TestContext.Current.CancellationToken));

        Assert.IsNotType<ApplyRejectedException>(error);
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    private static TreeApiClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

    /// <summary>Answers every request with one fixed response and records the last request.</summary>
    private sealed class StubHandler(
        HttpStatusCode status,
        string body,
        string mediaType = "application/problem+json") : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        }
    }
}
