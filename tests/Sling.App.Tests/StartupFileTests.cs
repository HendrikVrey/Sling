using System.IO;

namespace Sling.App.Tests;

/// <summary>
/// Which command-line argument Sling opens, when it is launched with some.
/// </summary>
/// <remarks>
/// This is the half of the file association that lives in the application. The installer
/// writes an open command of <c>Sling.exe "%1"</c>; if this stops picking the file out of
/// what arrives, the association silently becomes a lie - Sling launches, and shows a
/// different document from the one that was double-clicked.
/// </remarks>
public sealed class StartupFileTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("sling-startup-tests").FullName;

    [Fact]
    public void No_arguments_means_no_startup_file() =>
        Assert.Null(App.FirstReadableFile([]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_arguments_are_not_paths(string argument) =>
        Assert.Null(App.FirstReadableFile([argument]));

    [Fact]
    public void A_path_that_names_nothing_is_ignored() =>
        Assert.Null(App.FirstReadableFile([Path.Combine(_directory, "absent.http")]));

    [Fact]
    public void A_directory_is_not_a_document() =>
        Assert.Null(App.FirstReadableFile([_directory]));

    [Fact]
    public void An_existing_file_is_the_startup_file()
    {
        var path = Write("requests.http");

        Assert.Equal(path, App.FirstReadableFile([path]));
    }

    /// <summary>
    /// Explorer passes one path, but a shortcut, a drop onto the executable or a shell
    /// command can pass several, so the first <em>usable</em> one wins rather than the
    /// first one outright.
    /// </summary>
    [Fact]
    public void The_first_argument_that_names_a_file_wins()
    {
        var path = Write("second.http");

        Assert.Equal(path, App.FirstReadableFile([Path.Combine(_directory, "absent.http"), path]));
    }

    /// <summary>
    /// A relative argument is resolved against the working directory, because the window
    /// stores what it opened and every later save writes back to it.
    /// </summary>
    [Fact]
    public void A_relative_path_comes_back_absolute()
    {
        var path = Write("relative.http");
        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_directory);

            Assert.Equal(path, App.FirstReadableFile(["relative.http"]));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    /// <summary>
    /// An argument the operating system cannot turn into a path throws inside
    /// <see cref="Path.GetFullPath(string)"/> rather than returning false. It has to be
    /// caught, because the alternative is Sling failing to start over a stray argument.
    /// </summary>
    [Theory]
    [InlineData("::not-a-path")]
    [InlineData("a\0b")]
    [InlineData("|")]
    public void An_argument_that_cannot_be_a_path_does_not_throw(string argument) =>
        Assert.Null(App.FirstReadableFile([argument]));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "GET https://example.test/\n");
        return path;
    }
}
