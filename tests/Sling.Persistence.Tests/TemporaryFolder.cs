namespace Sling.Persistence.Tests;

/// <summary>
/// A real folder under the system temporary directory, deleted when the test finishes.
/// </summary>
/// <remarks>
/// A real one, not an abstraction over the file system. Everything these tests are about
/// - containment, atomic replacement, links, byte order marks - is behaviour of the file
/// system itself, and a fake would only ever confirm that the fake agrees with the
/// production code's assumptions.
/// </remarks>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes a file, creating whatever directories the relative path implies.</summary>
    public string Write(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);

        return full;
    }

    public string Read(string relative) => File.ReadAllText(System.IO.Path.Combine(Path, relative));

    public bool Exists(string relative) => File.Exists(System.IO.Path.Combine(Path, relative));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test folder left behind in TEMP is not worth failing a green run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
