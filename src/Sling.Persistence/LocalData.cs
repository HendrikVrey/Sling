namespace Sling.Persistence;

/// <summary>
/// Where Sling keeps the state that is its own rather than the user's.
/// </summary>
/// <remarks>
/// <para>
/// <c>%LOCALAPPDATA%\Sling</c>, and deliberately outside the workspace. A workspace is
/// somebody's git checkout: settings are not a fact about their API and have no business
/// in their diff, and a request log written there would be one <c>git add -A</c> from
/// being published. Defending that with a <c>.gitignore</c> entry is defending it with a
/// file the user can delete; keeping it out of the repository entirely leaves nothing to
/// defend.
/// </para>
/// <para>
/// The stores take their folder rather than reading this directly, which is what lets a
/// test point them somewhere disposable instead of at the profile of whoever is running
/// it.
/// </para>
/// </remarks>
public static class LocalData
{
    /// <summary>The folder the application uses.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sling");
}
