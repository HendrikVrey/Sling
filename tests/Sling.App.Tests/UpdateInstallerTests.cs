using System.IO;
using System.Security.Cryptography;
using Sling.App.Updates;
using Sling.Core.Updates;

namespace Sling.App.Tests;

/// <summary>
/// <see cref="UpdateInstaller"/>: an installer that changed after it was checked is never started.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _directory = UpdateInstaller.CreateDownloadDirectory();

    public void Dispose() => UpdateInstaller.Discard(_directory);

    [Fact]
    public void An_installer_changed_after_it_was_checked_is_not_started()
    {
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        var path = Path.Combine(_directory, "Sling-Setup-1.1.2.exe");
        File.WriteAllBytes(path, bytes);
        var expected = SHA256.HashData(bytes);
        File.AppendAllText(path, "tampered");

        var failure = Assert.Throws<UpdateCheckException>(() => UpdateInstaller.Launch(path, expected));

        Assert.Contains("changed on disk", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_folder_is_new_and_empty_and_goes_away()
    {
        var directory = UpdateInstaller.CreateDownloadDirectory();

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));

        UpdateInstaller.Discard(directory);

        Assert.False(Directory.Exists(directory));
    }
}
