using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;

namespace Sling.App;

public partial class App : Application
{
    /// <summary>
    /// The request file named on the command line, or null when Sling was started
    /// without one.
    /// </summary>
    /// <remarks>
    /// The installer registers a <c>Sling.http</c> ProgID whose open command is
    /// <c>Sling.exe "%1"</c>, so double-clicking a <c>.http</c> file in Explorer arrives
    /// here. Without this the association would launch Sling and show an empty document,
    /// which is worse than having no association at all: the file the user asked for
    /// would silently not be the file they got.
    /// </remarks>
    internal static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Before base, which is the call that processes StartupUri and therefore
        // constructs MainWindow. The window reads this while it initialises.
        StartupFile = FirstReadableFile(e.Args);

        base.OnStartup(e);

        // Applied unconditionally, even though App.xaml already merges the dark
        // ThemesDictionary. WPF-UI caches the current theme lazily, so until something
        // asks it, its cached answer is Unknown - and the DWM title-bar colour is set
        // from that cached value. Etch hit exactly this: a window whose caption stayed
        // light while its content was dark. One call at startup is the fix.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
    }

    /// <summary>
    /// The first argument that names a file on disk, resolved to a full path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explorer passes exactly one path, but a shortcut, a drag-and-drop onto the
    /// executable, or a shell command can pass several, so the first usable one wins
    /// rather than the first one outright.
    /// </para>
    /// <para>
    /// Existence is checked here rather than in the window because a path that cannot be
    /// resolved is dropped silently: Sling was launched, the user gets a working window,
    /// and an error dialog before the first frame would be a worse answer than an empty
    /// one. The window reports anything that goes wrong <em>reading</em> the file, which
    /// is the failure worth naming.
    /// </para>
    /// <para>
    /// Internal rather than private so it can be tested. Constructing an
    /// <see cref="Application"/> to exercise argument handling would be a test of WPF.
    /// </para>
    /// </remarks>
    internal static string? FirstReadableFile(IReadOnlyList<string> args)
    {
        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            try
            {
                var full = Path.GetFullPath(argument);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Not a path this process can name. Try the next argument.
            }
        }

        return null;
    }
}
