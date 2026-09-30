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

    public static TheoryData<Guid[], Guid> RootCases => new()
    {
        { [A], A },
        { [A, B], A },
        { [A, B, C], A },
    };

    public static TheoryData<Guid[], Guid, bool> DescendantCases => new()
    {
        // A root descends from nothing, not even itself.
        { [A], A, false },
        { [A], B, false },
        // Parent and any further ancestor.
        { [A, B], A, true },
        { [A, B, C], B, true },
        { [A, B, C], A, true },
        // Not its own descendant.
        { [A, B, C], C, false },
        // Unrelated: B is not on the path.
        { [A, C], B, false },
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
    [MemberData(nameof(RootCases))]
    public void Root_is_the_first_ancestor(Guid[] ancestors, Guid expected)
    {
        Assert.Equal(expected, Ancestry.RootOf(ancestors));
    }

    [Theory]
    [MemberData(nameof(DescendantCases))]
    public void Descendant_has_the_ancestor_on_its_path_before_itself(Guid[] ancestors, Guid ancestorId, bool expected)
    {
        Assert.Equal(expected, Ancestry.IsDescendantOf(ancestors, ancestorId));
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
    public void Empty_ancestor_list_has_no_root_and_no_ancestors()
    {
        Assert.Throws<ArgumentException>(() => Ancestry.RootOf([]));
        Assert.Throws<ArgumentException>(() => Ancestry.IsDescendantOf([], A));
    }

    [Fact]
    public void Empty_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Ancestry.ForRoot(Guid.Empty));
    }
}
