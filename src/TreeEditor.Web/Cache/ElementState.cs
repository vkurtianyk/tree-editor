namespace TreeEditor.Web.Cache;

/// <summary>Where a cached element stands against the database.</summary>
public enum ElementState
{
    /// <summary>As loaded, or as the last Apply stored it.</summary>
    Clean,

    /// <summary>Its value was changed locally and waits for Apply.</summary>
    Edited,

    /// <summary>Created locally and waits for Apply to insert it; its version is unknown until then.</summary>
    New,
}

/// <summary>
/// The outcome of an edit: the value the element now holds (normalised, with a local sibling suffix), or why the
/// value was refused.
/// </summary>
public sealed record ValueEditResult(string? Value, string? Error);
