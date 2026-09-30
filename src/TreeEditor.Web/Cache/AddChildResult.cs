namespace TreeEditor.Web.Cache;

/// <summary>
/// The outcome of adding a child: the new element's id and the value it holds (normalised, with a local sibling
/// suffix), or why the value was refused.
/// </summary>
public sealed record AddChildResult(Guid? Id, string? Value, string? Error);
