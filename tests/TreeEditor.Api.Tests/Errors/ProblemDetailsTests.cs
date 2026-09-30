using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TreeEditor.Api.Tests.Harness;
using TreeEditor.Contracts;

namespace TreeEditor.Api.Tests.Errors;

public sealed class ProblemDetailsTests(PostgresFixture postgres)
{
    private const string ProblemJson = "application/problem+json";

    [Fact]
    public async Task Malformed_parameter_is_a_400_problem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);

        using var response = await api.Client.GetAsync($"{ApiRoutes.Children}?parentId=not-a-guid", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("parentId", problem.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("afterValue=alpha", "afterId")]
    [InlineData("afterId=0199a000-0000-7000-8000-000000000001", "afterValue")]
    public async Task Half_a_children_cursor_is_a_400_validation_problem(string query, string missingParameter)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);

        using var response = await api.Client.GetAsync($"{ApiRoutes.Children}?{query}", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains(missingParameter, problem.Errors.Keys);
    }

    [Fact]
    public async Task Unknown_api_route_is_a_404_problem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = await ApiHarness.StartAsync(postgres, cancellationToken);

        using var response = await api.Client.GetAsync("/api/no-such-route", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }
}
