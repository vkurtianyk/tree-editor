namespace TreeEditor.Domain;

/// <summary>A sibling of the element whose value is being resolved.</summary>
public readonly record struct SiblingValue(Guid Id, string Value, bool IsDeleted);

/// <summary>
/// Values are unique among live siblings, compared trimmed and case-insensitively.
/// A colliding value gets the first free " (n)" suffix, counting from 1; when the result would exceed
/// <see cref="ElementValue.MaxLength"/>, the base is shortened, never the suffix.
/// The cache uses this for instant feedback; the server, which sees every sibling, has the final say.
/// </summary>
public static class SiblingSuffix
{
    /// <summary>
    /// The first candidate no live sibling holds. Deleted siblings don't count, nor does the element itself
    /// (<paramref name="self"/>, on rename).
    /// </summary>
    public static string Resolve(string value, IEnumerable<SiblingValue> siblings, Guid? self = null)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        var taken = siblings
            .Where(sibling => !sibling.IsDeleted && sibling.Id != self)
            .Select(sibling => ElementValue.SiblingKey(sibling.Value))
            .ToHashSet(StringComparer.Ordinal);
        return Resolve(value, taken.Contains);
    }

    /// <summary>The first candidate whose sibling key (trimmed, lowercase) <paramref name="isTaken"/> rejects.</summary>
    public static string Resolve(string value, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        return Candidates(value).First(candidate => !isTaken(ElementValue.SiblingKey(candidate)));
    }

    /// <summary>
    /// The values to try, best first and without end: the trimmed value, then "value (1)", "value (2)", …
    /// </summary>
    public static IEnumerable<string> Candidates(string value)
    {
        if (!ElementValue.TryNormalize(value, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return Enumerate(normalized);

        static IEnumerable<string> Enumerate(string value)
        {
            yield return value;
            for (var n = 1; ; n++)
            {
                var suffix = $" ({n})";
                var baseValue = value.Length + suffix.Length > ElementValue.MaxLength
                    ? value[..(ElementValue.MaxLength - suffix.Length)].TrimEnd()
                    : value;
                yield return baseValue + suffix;
            }
        }
    }
}
