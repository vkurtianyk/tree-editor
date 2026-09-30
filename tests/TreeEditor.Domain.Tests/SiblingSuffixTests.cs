namespace TreeEditor.Domain.Tests;

public class SiblingSuffixTests
{
    private static readonly Guid Self = Guid.Parse("0199a000-0000-7000-8000-00000000000a");

    // 251 characters: exactly room for a " (1)" suffix within 255.
    private static readonly string Fits = new('x', ElementValue.MaxLength - 4);
    private static readonly string Longest = new('x', ElementValue.MaxLength);

    public static TheoryData<string, string[], string> LiveSiblingCases => new()
    {
        // No collision: the value stays.
        { "Alpha", [], "Alpha" },
        { "Alpha", ["Beta"], "Alpha" },
        { "Alpha", ["Alpha (1)"], "Alpha" },
        // Collision: the first free " (n)", counting from 1.
        { "Alpha", ["Alpha"], "Alpha (1)" },
        { "Alpha", ["Alpha", "Alpha (1)"], "Alpha (2)" },
        { "Alpha", ["Alpha", "Alpha (2)"], "Alpha (1)" },
        { "Alpha", ["Alpha", "Alpha (1)", "Alpha (2)", "Alpha (3)"], "Alpha (4)" },
        // Compared case-insensitively and trimmed.
        { "Alpha", ["ALPHA"], "Alpha (1)" },
        { "alpha", ["Alpha", "ALPHA (1)"], "alpha (2)" },
        { "Alpha", ["  alpha  "], "Alpha (1)" },
        { "  Alpha  ", ["Alpha"], "Alpha (1)" },
        // A value that already ends in a suffix gets another one.
        { "Alpha (1)", ["Alpha (1)"], "Alpha (1) (1)" },
        // The result fits 255 characters: the base is shortened, not the suffix.
        { Fits, [Fits], Fits + " (1)" },
        { Longest, [Longest], Fits + " (1)" },
        { Longest, [Longest, Fits + " (1)"], new string('x', ElementValue.MaxLength - 4) + " (2)" },
        // Shortening doesn't leave a space before the suffix.
        { new string('x', 250) + " yyyy", [new string('x', 250) + " yyyy"], new string('x', 250) + " (1)" },
    };

    [Theory]
    [MemberData(nameof(LiveSiblingCases))]
    public void Colliding_value_gets_the_first_free_suffix(string value, string[] siblingValues, string expected)
    {
        var siblings = siblingValues.Select(sibling => new SiblingValue(Guid.CreateVersion7(), sibling, IsDeleted: false));

        var resolved = SiblingSuffix.Resolve(value, siblings);

        Assert.Equal(expected, resolved);
        Assert.True(resolved.Length <= ElementValue.MaxLength);
    }

    [Fact]
    public void Suffix_with_more_digits_shortens_the_base_further()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { Longest };
        for (var n = 1; n <= 9; n++)
        {
            taken.Add(new string('x', ElementValue.MaxLength - 4) + $" ({n})");
        }

        var resolved = SiblingSuffix.Resolve(Longest, taken.Contains);

        Assert.Equal(new string('x', ElementValue.MaxLength - 5) + " (10)", resolved);
    }

    [Fact]
    public void Deleted_siblings_do_not_collide()
    {
        SiblingValue[] siblings = [new(Guid.CreateVersion7(), "Alpha", IsDeleted: true)];

        Assert.Equal("Alpha", SiblingSuffix.Resolve("Alpha", siblings));
    }

    [Fact]
    public void Deleted_siblings_do_not_take_a_suffix()
    {
        SiblingValue[] siblings =
        [
            new(Guid.CreateVersion7(), "Alpha", IsDeleted: false),
            new(Guid.CreateVersion7(), "Alpha (1)", IsDeleted: true),
        ];

        Assert.Equal("Alpha (1)", SiblingSuffix.Resolve("Alpha", siblings));
    }

    [Theory]
    [InlineData("Alpha", "Alpha")]
    [InlineData("alpha", "ALPHA")]
    public void Rename_does_not_collide_with_the_element_itself(string current, string renamed)
    {
        SiblingValue[] siblings = [new(Self, current, IsDeleted: false)];

        Assert.Equal(renamed, SiblingSuffix.Resolve(renamed, siblings, self: Self));
    }

    [Fact]
    public void Rename_collides_with_other_siblings()
    {
        SiblingValue[] siblings =
        [
            new(Self, "Beta", IsDeleted: false),
            new(Guid.CreateVersion7(), "Alpha", IsDeleted: false),
        ];

        Assert.Equal("Alpha (1)", SiblingSuffix.Resolve("Alpha", siblings, self: Self));
    }

    [Fact]
    public void Candidates_start_with_the_value_then_count_from_one()
    {
        Assert.Equal(["Alpha", "Alpha (1)", "Alpha (2)"], SiblingSuffix.Candidates("  Alpha ").Take(3));
    }

    [Fact]
    public void Taken_check_receives_lowercase_keys()
    {
        var asked = new List<string>();

        SiblingSuffix.Resolve("Alpha", key =>
        {
            asked.Add(key);
            return key == "alpha";
        });

        Assert.Equal(["alpha", "alpha (1)"], asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_value_is_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => SiblingSuffix.Resolve(value, []));
        Assert.Throws<ArgumentException>(() => SiblingSuffix.Candidates(value).First());
    }

    [Fact]
    public void Sibling_key_is_trimmed_and_lowercase()
    {
        Assert.Equal("alpha beta", ElementValue.SiblingKey("  Alpha BETA "));
    }
}
