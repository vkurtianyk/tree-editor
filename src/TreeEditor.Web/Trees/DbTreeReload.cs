namespace TreeEditor.Web.Trees;

/// <summary>
/// Tells DBTreeView that the database changed under it (after a Reset), so it drops every loaded level and
/// selection and reloads from the roots.
/// </summary>
public sealed class DbTreeReload
{
    public event Action? Requested;

    public void Request() => Requested?.Invoke();
}
