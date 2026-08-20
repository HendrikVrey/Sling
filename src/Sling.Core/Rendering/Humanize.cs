using System.Globalization;

namespace Sling.Core.Rendering;

/// <summary>
/// Formats the two numbers a response is judged by. Pure, and in <c>Sling.Core</c>, so
/// the status bar and the response pane cannot drift into showing the same figure two
/// different ways.
/// </summary>
public static class Humanize
{
    private const long Kilobyte = 1024;
    private const long Megabyte = Kilobyte * 1024;

    /// <summary>
    /// A byte count at the precision a person can act on: exact below 1 KB, one decimal
    /// above it.
    /// </summary>
    public static string Size(long bytes)
    {
        if (bytes < Kilobyte)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < Megabyte)
        {
            return ((double)bytes / Kilobyte).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return ((double)bytes / Megabyte).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// Whole milliseconds under a second, seconds above it. Sub-millisecond precision
    /// would be noise: it is smaller than the variance between two identical requests.
    /// </summary>
    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed.TotalMilliseconds < 1000)
        {
            return Math.Round(elapsed.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        return elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }
}
