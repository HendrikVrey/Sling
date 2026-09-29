using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Sling.Core.Updates;

namespace Sling.Http;

/// <summary>
/// Asks GitHub for the newest release of Sling and downloads its installer.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the application because this project is the one that touches the
/// network (<c>Sling.md</c> §3). It is the only request Sling makes that the user did not
/// write, which is why it has none of the request runner's machinery: no cookies, no
/// environments, no tokens.
/// </para>
/// <para>
/// Two requests, both to GitHub, both carrying nothing about the user: no text, no file
/// name, no identifier. The only header that says anything is the <c>User-Agent</c>,
/// which GitHub's API requires and which names Sling and its version. The README and the
/// settings panel say the same thing in the same words.
/// </para>
/// <para>
/// It runs only when the user has said yes to update checks, or pressed "Check now".
/// Nothing here decides that; <c>MainWindow.Updates</c> does.
/// </para>
/// </remarks>
public sealed class UpdateClient : IDisposable
{
    /// <summary>GitHub's answer for the newest published, non-prerelease release.</summary>
    public static readonly Uri LatestReleaseUrl = new("https://api.github.com/repos/HendrikVrey/Sling/releases/latest");

    /// <summary>Every installer Sling will download starts with this.</summary>
    public const string DownloadPrefix = "https://github.com/HendrikVrey/Sling/releases/download/";

    /// <summary>The installer's name on every release.</summary>
    public const string InstallerName = "Sling-Setup.exe";

    /// <summary>A release description is a few kilobytes; anything past this is not one.</summary>
    private const int MaxReplyBytes = 1024 * 1024;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the download may sit with nothing arriving before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Not a limit on the whole download, which on a slow line can rightly take minutes.
    /// </remarks>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    /// <summary>Creates a client over the real network.</summary>
    /// <param name="current">The running version, which the <c>User-Agent</c> names.</param>
    public UpdateClient(ReleaseVersion current)
        : this(current, new SocketsHttpHandler
        {
            // HTTPS to HTTPS only; GitHub's download address redirects once to its file
            // host, and the checksum is what proves the file, wherever it came from.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
        })
    {
    }

    /// <summary>Creates a client over <paramref name="handler"/>, for tests.</summary>
    internal UpdateClient(ReleaseVersion current, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(handler);

        _http = new HttpClient(handler, disposeHandler: true)
        {
            // Each call sets its own deadline.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sling", current.ToString()));
    }

    /// <summary>Asks GitHub for the newest release.</summary>
    /// <exception cref="UpdateCheckException">The answer was not a usable release, or never came.</exception>
    public async Task<LatestRelease> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateCheckException("No version of Sling has been released yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException(
                    $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            }

            if (response.Content.Headers.ContentLength > MaxReplyBytes)
            {
                throw new UpdateCheckException("GitHub's reply was far too large to be a release.");
            }

            var json = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);

            return LatestRelease.Parse(json, InstallerName, DownloadPrefix);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException("Sling could not reach GitHub. Check the connection and try again.", ex);
        }
        catch (IOException ex)
        {
            // The connection dropped while the reply was being read (HttpIOException).
            throw new UpdateCheckException("The connection to GitHub dropped. Try again later.", ex);
        }
    }

    /// <summary>
    /// Downloads the release's installer into <paramref name="directory"/> and proves it
    /// is the file GitHub holds.
    /// </summary>
    /// <param name="release">The release to download.</param>
    /// <param name="directory">An existing directory that belongs to this download alone.</param>
    /// <param name="progress">Receives the fraction downloaded, from 0 to 1.</param>
    /// <param name="cancellationToken">Stops the download; the partial file is deleted.</param>
    /// <returns>The path of the verified installer.</returns>
    /// <exception cref="UpdateCheckException">
    /// The download failed, or what arrived is not the file GitHub described. Either way
    /// nothing is left on disk.
    /// </exception>
    public async Task<string> DownloadAsync(
        LatestRelease release,
        string directory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, $"Sling-Setup-{release.Version}.exe");

        try
        {
            await DownloadCoreAsync(release, path, progress, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private async Task DownloadCoreAsync(
        LatestRelease release,
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        try
        {
            using var response = await _http
                .GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException(
                    $"The download failed: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (response.Content.Headers.ContentLength is { } length && length != release.InstallerBytes)
            {
                throw new UpdateCheckException("The download is not the size GitHub said it would be, so it was discarded.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);

            await using (source.ConfigureAwait(false))
            {
                // CreateNew: the directory is this download's own, so an existing file
                // there is something that should not be, and is not written through.
                var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var reported = -1;

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);

                        if (read == 0)
                        {
                            break;
                        }

                        received += read;

                        if (received > release.InstallerBytes)
                        {
                            throw new UpdateCheckException("The download is larger than GitHub said it would be, so it was discarded.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);

                        // Something arrived, so the line is alive: the stall clock restarts.
                        stall.CancelAfter(StallTimeout);

                        var percent = (int)(received * 100 / release.InstallerBytes);

                        if (percent != reported)
                        {
                            reported = percent;
                            progress?.Report(received / (double)release.InstallerBytes);
                        }
                    }

                    if (received != release.InstallerBytes)
                    {
                        throw new UpdateCheckException("The download stopped before the whole installer arrived. Try again.");
                    }

                    await target.FlushAsync(stall.Token).ConfigureAwait(false);
                }
            }

            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), release.Sha256.Span))
            {
                throw new UpdateCheckException(
                    "The downloaded installer does not match the checksum GitHub published, so it was deleted and not run.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("The download stalled, so it was stopped. Try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException("The download failed. Check the connection and try again.", ex);
        }
        catch (IOException ex)
        {
            throw new UpdateCheckException($"The installer could not be saved: {ex.Message}", ex);
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxReplyBytes)
                {
                    throw new UpdateCheckException("GitHub's reply was far too large to be a release.");
                }

                buffer.Write(chunk, 0, read);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is in %TEMP% and is swept on the next check; nothing more to do.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
