namespace TreeEditor.Domain.Tests;

public class ElementValueTests
{
    private static readonly string Longest = new('x', ElementValue.MaxLength);

    public static TheoryData<string, string> ValidCases => new()
    {
        { "Alpha", "Alpha" },
        { "  Alpha  ", "Alpha" },
        { "\tAlpha beta\n", "Alpha beta" },
        // Inner whitespace stays.
        { "Alpha  beta", "Alpha  beta" },
        { Longest, Longest },
        // The limit applies after trimming.
        { $"  {Longest}  ", Longest },
    };

    public static TheoryData<string> InvalidCases => new()
    {
        "",
        "   ",
        "\t\n",
        Longest + "x",
    };

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void Valid_value_is_trimmed(string value, string expected)
    {
        Assert.True(ElementValue.TryNormalize(value, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
        Assert.Null(ElementValue.Validate(value));
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Missing_or_too_long_value_is_invalid(string value)
    {
        Assert.False(ElementValue.TryNormalize(value, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(error, ElementValue.Validate(value));
    }

    [Fact]
    public void Null_value_is_invalid()
    {
        Assert.False(ElementValue.TryNormalize(null, out _, out _));
        Assert.Equal("A value is required.", ElementValue.Validate(null));
    }

    [Fact]
    public void Error_says_what_is_wrong()
    {
        Assert.Equal("A value is required.", ElementValue.Validate("  "));
        Assert.Equal("A value can have at most 255 characters.", ElementValue.Validate(Longest + "x"));
    }
}
