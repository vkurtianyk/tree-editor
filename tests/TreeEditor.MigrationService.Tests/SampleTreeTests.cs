using TreeEditor.MigrationService.Seeding;

namespace TreeEditor.MigrationService.Tests;

public class SampleTreeTests
{
    // Known ids: the same on every run and machine (fixed seed, pinned Bogus). Demo and smoke tests rely on them.
    private static readonly Guid ChainRoot = Guid.Parse("019b76da-a800-709f-b3bf-d4c669178afe");
    private static readonly Guid ChainStart = Guid.Parse("019b76da-a805-73fe-9941-734233ec6816");
    private static readonly Guid ChainEnd = Guid.Parse("019b76da-a8de-7489-9cc7-0abfe336c739");
    private static readonly Guid FirstWide = Guid.Parse("019b76da-a8df-75ee-af0f-faac623e41b7");

    private static readonly (Guid Id, string Value)[] Roots =
    [
        (ChainRoot, "Industrial"),
        (Guid.Parse("019b76da-a801-7926-9a3c-17b775163b37"), "Home"),
        (Guid.Parse("019b76da-a802-70b7-b288-4b83d0e0c0a1"), "Electronics"),
        (Guid.Parse("019b76da-a803-7b84-87fc-49be8a32415c"), "Garden"),
        (Guid.Parse("019b76da-a804-7cc0-9ad7-6b37f6d4184d"), "Computers"),
    ];

    private static readonly IReadOnlyList<SeedRow> Tree = SampleTree.Generate(SampleTree.DefaultSize);

    private static readonly Dictionary<Guid, SeedRow> ById = Tree.ToDictionary(row => row.Id);

    private static readonly Dictionary<Guid, int> ChildCounts = Tree
        .Where(row => row.ParentId is not null)
        .GroupBy(row => row.ParentId!.Value)
        .ToDictionary(group => group.Key, group => group.Count());

    [Theory]
    [InlineData(SampleTree.MinSize)]
    [InlineData(SampleTree.DefaultSize)]
    public void Generates_exactly_the_requested_number_of_elements(int size)
    {
        Assert.Equal(size, SampleTree.Generate(size).Count);
    }

    [Theory]
    [InlineData(SampleTree.MinSize - 1)]
    [InlineData(SampleTree.MaxSize + 1)]
    public void Size_outside_the_supported_range_is_rejected(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleTree.Generate(size));
    }

    [Fact]
    public void Ids_are_unique_guid_v7()
    {
        Assert.Equal(Tree.Count, ById.Count);
        Assert.All(Tree, row => Assert.Equal(7, row.Id.Version));
    }

    [Fact]
    public void Has_the_five_known_roots()
    {
        Assert.Equal(Roots, Tree.Where(row => row.ParentId is null).Select(row => (row.Id, row.Value)));
    }

    [Fact]
    public void Ancestors_are_the_parent_ancestors_plus_own_id()
    {
        Assert.All(Tree, row =>
        {
            Guid[] expected = row.ParentId is { } parentId ? [.. ById[parentId].Ancestors, row.Id] : [row.Id];
            Assert.Equal(expected, row.Ancestors);
        });
    }

    [Fact]
    public void One_chain_is_200_levels_deep()
    {
        var chainEnd = ById[ChainEnd];
        Assert.Equal(SampleTree.ChainDepth, chainEnd.Ancestors.Length);
        Assert.Equal([ChainRoot, ChainStart], chainEnd.Ancestors[..2]);
        Assert.StartsWith("Level 200 ", chainEnd.Value, StringComparison.Ordinal);
        Assert.All(chainEnd.Ancestors[1..^1], id => Assert.Equal(1, ChildCounts[id]));
        Assert.DoesNotContain(ChainEnd, ChildCounts.Keys);
    }

    [Fact]
    public void Everything_off_the_chain_is_at_most_12_levels_deep()
    {
        var chain = ById[ChainEnd].Ancestors.ToHashSet();
        Assert.All(
            Tree.Where(row => !chain.Contains(row.Id)),
            row => Assert.InRange(row.Ancestors.Length, 1, SampleTree.MaxDepth));
    }

    [Fact]
    public void A_few_elements_have_5k_to_10k_children_and_the_rest_at_most_20()
    {
        var wide = ChildCounts.Where(count => count.Value > SampleTree.MaxChildren).ToList();

        Assert.Equal(SampleTree.WideCount, wide.Count);
        Assert.Contains(FirstWide, wide.Select(count => count.Key));
        Assert.All(wide, count => Assert.InRange(count.Value, SampleTree.MinWideChildren, SampleTree.MaxWideChildren));
    }

    [Fact]
    public void Values_are_trimmed_ascii_and_at_most_255_characters()
    {
        Assert.All(Tree, row =>
        {
            Assert.NotEmpty(row.Value);
            Assert.Equal(row.Value.Trim(), row.Value);
            Assert.InRange(row.Value.Length, 1, SampleTree.MaxValueLength);
            Assert.True(row.Value.All(char.IsAscii), row.Value);
        });
    }

    [Fact]
    public void Sibling_values_are_unique_ignoring_case_with_suffixes_on_clashes()
    {
        var duplicates = Tree
            .GroupBy(row => (row.ParentId, Value: row.Value.ToLowerInvariant()))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        Assert.Empty(duplicates);
        Assert.Contains(Tree, row => row.Value.EndsWith(" (1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Generating_again_gives_the_same_tree()
    {
        Assert.Equal(Project(Tree), Project(SampleTree.Generate(SampleTree.DefaultSize)));
    }

    [Fact]
    public void Roots_chain_and_wide_elements_are_the_same_at_every_size()
    {
        var smaller = SampleTree.Generate(SampleTree.MinSize);

        Assert.Equal(Project(FixedPart(Tree)), Project(FixedPart(smaller)));
    }

    // Roots, their children, the chain and the wide elements' children: generated before the size-dependent rest.
    private static IEnumerable<SeedRow> FixedPart(IReadOnlyList<SeedRow> tree)
    {
        var childCounts = tree.Where(row => row.ParentId is not null).CountBy(row => row.ParentId!.Value).ToDictionary();
        return tree.Where(row =>
            row.Ancestors.Length <= 2
            || row.Ancestors[1] == ChainStart
            || childCounts.GetValueOrDefault(row.ParentId!.Value) > SampleTree.MaxChildren);
    }

    private static IEnumerable<(Guid Id, Guid? ParentId, string Value)> Project(IEnumerable<SeedRow> rows) =>
        rows.Select(row => (row.Id, row.ParentId, row.Value));
}
