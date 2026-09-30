namespace TreeEditor.Domain.Tests;

public class AncestryTests
{
    private static readonly Guid A = Guid.Parse("0199a000-0000-7000-8000-00000000000a");
    private static readonly Guid B = Guid.Parse("0199a000-0000-7000-8000-00000000000b");
    private static readonly Guid C = Guid.Parse("0199a000-0000-7000-8000-00000000000c");

    public static TheoryData<Guid[], Guid, Guid[]> ChildCases => new()
    {
        { [A], B, [A, B] },
        { [A, B], C, [A, B, C] },
    };

    [Fact]
    public void Root_ancestors_are_just_its_own_id()
    {
        Assert.Equal([A], Ancestry.ForRoot(A));
    }

    [Theory]
    [MemberData(nameof(ChildCases))]
    public void Child_ancestors_are_parent_ancestors_plus_own_id(Guid[] parentAncestors, Guid id, Guid[] expected)
    {
        Assert.Equal(expected, Ancestry.ForChild(parentAncestors, id));
    }

    [Theory]
    [MemberData(nameof(ChildCases))]
    public void Root_is_the_first_ancestor(Guid[] parentAncestors, Guid id, Guid[] _)
    {
        Assert.Equal(A, Ancestry.RootOf(Ancestry.ForChild(parentAncestors, id)));
    }

    [Fact]
    public void Element_cannot_be_its_own_ancestor()
    {
        Assert.Throws<ArgumentException>(() => Ancestry.ForChild([A, B], A));
    }

    [Fact]
    public void Parent_ancestors_must_not_be_empty()
    {
        Assert.Throws<ArgumentException>(() => Ancestry.ForChild([], A));
    }

    [Fact]
    public void Empty_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Ancestry.ForRoot(Guid.Empty));
    }
}
