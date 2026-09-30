using System.Diagnostics.CodeAnalysis;

namespace TreeEditor.Domain;

/// <summary>
/// Element values: stored trimmed, required, at most <see cref="MaxLength"/> characters.
/// Siblings compare them trimmed and case-insensitively, like the database's unique index on <c>lower(value)</c>.
/// </summary>
public static class ElementValue
{
    public const int MaxLength = 255;

    /// <summary>The trimmed value when it's valid; otherwise why it isn't.</summary>
    public static bool TryNormalize(
        string? value,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            normalized = null;
            error = "A value is required.";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            normalized = null;
            error = $"A value can have at most {MaxLength} characters.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    /// <summary>Why the value is invalid, or null when it's valid.</summary>
    public static string? Validate(string? value) => TryNormalize(value, out _, out var error) ? null : error;

    /// <summary>What siblings compare: the value trimmed and lowercase.</summary>
    public static string SiblingKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().ToLowerInvariant();
    }
}
