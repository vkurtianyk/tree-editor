using System.Net;
using System.Net.Http.Json;
using TreeEditor.Contracts;
using TreeEditor.Web.Api;
using TreeEditor.Web.Trees;

namespace TreeEditor.Web.Tests.Trees;

public sealed class DbTreeLevelTests
{
    private static readonly NodeListItem Alpha = Item(1, "Alpha");
    private static readonly NodeListItem Beta = Item(2, "Beta");
    private static readonly NodeListItem Gamma = Item(3, "Gamma");

    [Fact]
    public async Task Element_listed_again_on_a_later_page_keeps_its_one_row()
    {
        // Another tab renamed Alpha to a value past the cursor between the two pages, so page 2 lists it again.
        var api = Api(
            Page(new ChildrenPage([Alpha, Beta], HasMore: true, new ChildrenCursor("beta", Beta.Id))),
            Page(new ChildrenPage([Gamma, Alpha with { Value = "Omega" }], HasMore: false, Next: null)));
        var level = new DbTreeLevel(parentId: null);

        await level.LoadNextPageAsync(api, TestContext.Current.CancellationToken);
        await level.LoadNextPageAsync(api, TestContext.Current.CancellationToken);

        Assert.Equal([Alpha, Beta, Gamma], level.Nodes.Select(node => node.Item));
        Assert.False(level.HasMore);
    }

    private static NodeListItem Item(int id, string value) =>
        new(Guid.Parse($"0199a000-0000-7000-8000-{id:x12}"), value, IsDeleted: false, HasChildren: false);

    private static Func<HttpResponseMessage> Page(ChildrenPage page) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) };

    /// <summary>A client whose requests get the responses in order, one each.</summary>
    private static TreeApiClient Api(params Func<HttpResponseMessage>[] responses) =>
        new(new HttpClient(new QueueHandler(new Queue<Func<HttpResponseMessage>>(responses)))
        {
            BaseAddress = new Uri("http://localhost/"),
        });

    private sealed class QueueHandler(Queue<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responses.TryDequeue(out var respond)
                ? Task.FromResult(respond())
                : throw new InvalidOperationException($"No response left for {request.RequestUri}.");
    }
}
