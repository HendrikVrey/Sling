using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.IO;
using Sling.Core.Updates;

namespace Sling.App.Updates;

/// <summary>
/// Where a downloaded installer is kept, and how it is started.
/// </summary>
/// <remarks>
/// <para>
/// Each download gets a folder of its own under <c>%TEMP%</c>, named with a fresh GUID and
/// created by Sling, so nothing else can have put a file where the installer is about to
/// be written. Old folders are swept at the next check: an installer is about 100 MB and
/// Windows' own temp cleanup is not something to rely on.
/// </para>
/// <para>
/// The installer is started as a plain executable, with no shell and no arguments, and
/// shown as it always is: the user chose a visible install, so it asks its usual questions
/// and they can still cancel it. It runs per user and needs no elevation.
/// </para>
/// </remarks>
internal static class UpdateInstaller
{
    private const string FolderPrefix = "Sling-Update-";

    /// <summary>How old a leftover download folder must be before it is swept.</summary>
    /// <remarks>
    /// A day, so a download from a few minutes ago whose installer is still running is
    /// never pulled out from under it.
    /// </remarks>
    private static readonly TimeSpan SweepAge = TimeSpan.FromDays(1);

    /// <summary>Creates a new, empty folder for one download.</summary>
    public static string CreateDownloadDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>Deletes download folders left by earlier updates. Never throws.</summary>
    public static void SweepOldDownloads(DateTimeOffset now)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), FolderPrefix + "*"))
            {
                try
                {
                    if (now - Directory.GetCreationTimeUtc(folder) >= SweepAge)
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use, or not ours to delete. The next sweep tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp folder itself could not be listed; the next check tries again.
        }
    }

    /// <summary>
    /// How many other copies of Sling are running.
    /// </summary>
    /// <remarks>
    /// Sling allows more than one window, each its own process, and only the one that runs
    /// the update can settle its own unsaved work. The installer's Restart Manager would
    /// close the others, and WPF does not honour a cancelled close at that point, so an
    /// unsaved document in another window would be lost. The update waits for them instead.
    /// </remarks>
    public static int CountOtherInstances()
    {
        using var current = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(current.ProcessName);

        try
        {
            return others.Count(process => process.Id != current.Id);
        }
        finally
        {
            foreach (var process in others)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Deletes one download folder, for a download that was abandoned.</summary>
    public static void Discard(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept at the next check.
        }
    }

    /// <summary>Starts the installer, if it is still the file that was verified.</summary>
    /// <param name="installerPath">The downloaded installer.</param>
    /// <param name="sha256">The checksum it was verified against as it arrived.</param>
    /// <remarks>
    /// The download was hashed as it streamed in, and then the file sat closed on disk. So
    /// it is hashed again here through a handle that refuses writers and deletion, and that
    /// handle stays open until Windows has started the process: nothing can replace the
    /// file between the check and the start. Reading a hundred megabytes again costs a
    /// fraction of a second, once per update.
    /// </remarks>
    /// <exception cref="UpdateCheckException">The file changed, or Windows would not start it.</exception>
    public static void Launch(string installerPath, ReadOnlyMemory<byte> sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        try
        {
            using var pinned = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(pinned), sha256.Span))
            {
                throw new UpdateCheckException("The installer changed on disk after it was checked, so it was not run.");
            }

            using var process = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(installerPath),
            });

            if (process is null)
            {
                throw new UpdateCheckException("Windows did not start the installer.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateCheckException($"The installer could not be started: {ex.Message}", ex);
        }
    }
}
