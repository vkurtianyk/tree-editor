namespace TreeEditor.Web.Trees;

/// <summary>
/// Tells DBTreeView that the database changed under it (after a Reset or an Apply), so it reloads the levels it
/// shows.
/// </summary>
public sealed class DbTreeReload
{
    public event Action? Requested;

    public void Request() => Requested?.Invoke();
}
