using System.Globalization;
using System.Text.Json;

namespace Sling.Core.Updates;

/// <summary>
/// The newest published release, as GitHub describes it, reduced to what an update needs.
/// </summary>
/// <param name="Version">The release's version, read from its tag.</param>
/// <param name="InstallerUrl">Where the installer is downloaded from.</param>
/// <param name="InstallerBytes">The installer's size, as GitHub reports it.</param>
/// <param name="Sha256">The installer's SHA-256, as GitHub computed it on upload.</param>
public sealed record LatestRelease(ReleaseVersion Version, Uri InstallerUrl, long InstallerBytes, ReadOnlyMemory<byte> Sha256)
{
    /// <summary>
    /// The largest installer Sling will download.
    /// </summary>
    /// <remarks>
    /// The real one is about 100 MB, because it carries both architectures. The ceiling is
    /// there so that a reply claiming something absurd cannot fill the disk.
    /// </remarks>
    public const long MaxInstallerBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Reads GitHub's <c>releases/latest</c> reply.
    /// </summary>
    /// <param name="json">The body of the reply.</param>
    /// <param name="assetName">The installer's file name, <c>Sling-Setup.exe</c>.</param>
    /// <param name="downloadPrefix">
    /// What the installer's address must start with:
    /// <c>https://github.com/HendrikVrey/Sling/releases/download/</c>. Anything else is
    /// refused, so a reply cannot point the download somewhere Sling was never meant to go.
    /// </param>
    /// <exception cref="UpdateCheckException">The reply is not a release Sling can install.</exception>
    public static LatestRelease Parse(string json, string assetName, string downloadPrefix)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new UpdateCheckException("GitHub's reply was not a release.");
            }

            // releases/latest never returns either of these, but the rule it enforces is
            // the rule that matters: only a published, stable release is offered.
            if (Flag(root, "draft") || Flag(root, "prerelease"))
            {
                throw new UpdateCheckException("The newest release is not a published, stable one.");
            }

            var version = ReleaseVersion.TryParse(Text(root, "tag_name"))
                ?? throw new UpdateCheckException("The newest release's tag is not a version.");

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateCheckException($"Release {version} has no files.");
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && string.Equals(Text(asset, "name"), assetName, StringComparison.Ordinal))
                {
                    return FromAsset(version, asset, downloadPrefix);
                }
            }

            throw new UpdateCheckException($"Release {version} has no {assetName}.");
        }
        catch (JsonException)
        {
            throw new UpdateCheckException("GitHub's reply could not be read.");
        }
    }

    private static LatestRelease FromAsset(ReleaseVersion version, JsonElement asset, string downloadPrefix)
    {
        var address = Text(asset, "browser_download_url");

        // Checked on the address as Uri normalises it, never on the raw text: a raw
        // prefix check passes ".../releases/download/../../../../someone/else/..." and
        // "%2e%2e" segments, which Uri then resolves to another repository entirely. The
        // checksum comes from the same reply, so this check is what pins the source.
        if (address is null
            || !Uri.TryCreate(address, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !url.AbsoluteUri.StartsWith(downloadPrefix, StringComparison.Ordinal)
            || url.AbsolutePath.Contains("..", StringComparison.Ordinal))
        {
            throw new UpdateCheckException($"Release {version}'s installer is not where Sling's releases live.");
        }

        if (!asset.TryGetProperty("size", out var sizeElement)
            || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size)
            || size <= 0
            || size > MaxInstallerBytes)
        {
            throw new UpdateCheckException($"Release {version}'s installer has an implausible size.");
        }

        // Without a checksum there is nothing to prove the download is the file GitHub
        // holds, so the update is not offered at all. GitHub has published one for every
        // release asset since 2025.
        var digest = Text(asset, "digest");
        const string Prefix = "sha256:";

        if (digest is null
            || !digest.StartsWith(Prefix, StringComparison.Ordinal)
            || digest.Length != Prefix.Length + 64)
        {
            throw new UpdateCheckException($"Release {version} has no checksum to verify its installer against.");
        }

        byte[] sha256;

        try
        {
            sha256 = Convert.FromHexString(digest.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            throw new UpdateCheckException($"Release {version}'s checksum is not a checksum.");
        }

        return new LatestRelease(version, url, size, sha256);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The size as a person reads it.</summary>
    public string DescribeSize() =>
        string.Create(CultureInfo.CurrentCulture, $"{InstallerBytes / (1024.0 * 1024.0):0} MB");
}

/// <summary>
/// An update check or download that did not work, with a sentence saying why.
/// </summary>
/// <remarks>
/// The message is written for the status bar, never for a log only: every one of these
/// can reach the user when they pressed "Check now" themselves.
/// </remarks>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message)
        : base(message)
    {
    }

    public UpdateCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UpdateCheckException()
    {
    }
}
