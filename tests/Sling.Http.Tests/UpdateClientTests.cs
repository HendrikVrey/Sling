using System.Net;
using System.Security.Cryptography;
using Sling.Core.Updates;

namespace Sling.Http.Tests;

/// <summary>
/// <see cref="LatestRelease.Parse"/> and <see cref="UpdateClient"/>, against canned replies:
/// no test here reaches the network.
/// </summary>
public sealed class UpdateClientTests : IDisposable
{
    private static readonly byte[] Installer = CreateInstaller();

    private static readonly ReleaseVersion Running = ReleaseVersion.TryParse("1.1.1-dev.3")!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "etch-update-tests", Guid.NewGuid().ToString("N"));

    public UpdateClientTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_real_release_reply_is_read()
    {
        var release = LatestRelease.Parse(ReleaseJson(), UpdateClient.InstallerName, UpdateClient.DownloadPrefix);

        Assert.Equal("1.1.2", release.Version.ToString());
        Assert.Equal(Installer.Length, release.InstallerBytes);
        Assert.Equal(SHA256.HashData(Installer), release.Sha256.ToArray());
        Assert.Equal("https://github.com/HendrikVrey/Sling/releases/download/v1.1.2/Sling-Setup.exe", release.InstallerUrl.ToString());
    }

    [Theory]
    [InlineData("https://github.com/SomebodyElse/Sling/releases/download/v1.1.2/Sling-Setup.exe")]
    [InlineData("http://github.com/HendrikVrey/Sling/releases/download/v1.1.2/Sling-Setup.exe")]
    [InlineData("https://example.com/HendrikVrey/Sling/releases/download/v1.1.2/Sling-Setup.exe")]
    public void An_installer_anywhere_else_is_refused(string url) =>
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(url: url)));

    [Theory]
    [InlineData("https://github.com/HendrikVrey/Sling/releases/download/../../../../SomebodyElse/repo/releases/download/v1.1.2/Sling-Setup.exe")]
    [InlineData("https://github.com/HendrikVrey/Sling/releases/download/%2e%2e/%2e%2e/%2e%2e/%2e%2e/SomebodyElse/x/Sling-Setup.exe")]
    public void An_address_that_only_starts_in_the_right_place_is_refused(string url) =>
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(url: url)));

    [Fact]
    public void A_size_that_is_not_a_number_is_refused_as_a_sentence() =>
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson().Replace($"\"size\": {Installer.Length}", "\"size\": \"big\"", StringComparison.Ordinal)));

    [Fact]
    public async Task A_connection_that_drops_mid_reply_is_a_sentence()
    {
        using var client = new UpdateClient(Running, new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DroppingStream()),
        }));

        await Assert.ThrowsAsync<UpdateCheckException>(() => client.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_release_without_a_checksum_is_refused() =>
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(digest: null)));

    [Fact]
    public void A_checksum_that_is_not_sha256_is_refused() =>
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(digest: "md5:0123456789abcdef0123456789abcdef")));

    [Fact]
    public void A_prerelease_or_draft_is_refused()
    {
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(extra: "\"prerelease\": true,")));
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(extra: "\"draft\": true,")));
    }

    [Fact]
    public void A_release_without_the_installer_is_refused() =>
        Assert.Throws<UpdateCheckException>(() => Parse(ReleaseJson(name: "Sling.zip")));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\": \"latest\", \"assets\": []}")]
    public void A_reply_that_is_not_a_release_is_refused(string json) =>
        Assert.Throws<UpdateCheckException>(() => Parse(json));

    [Fact]
    public async Task The_newest_release_is_fetched_with_a_user_agent()
    {
        HttpRequestMessage? seen = null;
        using var client = new UpdateClient(Running, new CannedHandler(request =>
        {
            seen = request;
            return Json(ReleaseJson());
        }));

        var release = await client.GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.1.2", release.Version.ToString());
        Assert.NotNull(seen);
        Assert.Equal(UpdateClient.LatestReleaseUrl, seen.RequestUri);
        Assert.Contains(seen.Headers.UserAgent, product => product.Product?.Name == "Sling");
    }

    [Fact]
    public async Task A_refusal_from_github_is_a_sentence()
    {
        using var client = new UpdateClient(Running, new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var failure = await Assert.ThrowsAsync<UpdateCheckException>(() => client.GetLatestAsync(TestContext.Current.CancellationToken));

        Assert.Contains("403", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_download_is_kept()
    {
        var release = Parse(ReleaseJson());
        using var client = new UpdateClient(Running, new CannedHandler(_ => Bytes(Installer)));

        var path = await client.DownloadAsync(release, _directory, progress: null, TestContext.Current.CancellationToken);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_checksum_is_deleted()
    {
        var release = Parse(ReleaseJson());
        var tampered = (byte[])Installer.Clone();
        tampered[100] ^= 0xFF;
        using var client = new UpdateClient(Running, new CannedHandler(_ => Bytes(tampered)));

        var failure = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.DownloadAsync(release, _directory, progress: null, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task A_download_longer_than_promised_is_stopped_and_deleted()
    {
        var release = Parse(ReleaseJson());
        var longer = Installer.Concat(new byte[4096]).ToArray();
        using var client = new UpdateClient(Running, new CannedHandler(_ => Bytes(longer, declareLength: false)));

        await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.DownloadAsync(release, _directory, progress: null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task A_download_cut_short_is_deleted()
    {
        var release = Parse(ReleaseJson());
        using var client = new UpdateClient(Running, new CannedHandler(_ => Bytes(Installer[..1000], declareLength: false)));

        await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.DownloadAsync(release, _directory, progress: null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    private static LatestRelease Parse(string json) =>
        LatestRelease.Parse(json, UpdateClient.InstallerName, UpdateClient.DownloadPrefix);

    private static string ReleaseJson(
        string url = "https://github.com/HendrikVrey/Sling/releases/download/v1.1.2/Sling-Setup.exe",
        string? digest = "",
        string name = "Sling-Setup.exe",
        string extra = "")
    {
        digest = digest == "" ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Installer)) : digest;
        var digestProperty = digest is null ? string.Empty : $"\"digest\": \"{digest}\",";

        return $$"""
            {
              "tag_name": "v1.1.2",
              {{extra}}
              "html_url": "https://github.com/HendrikVrey/Sling/releases/tag/v1.1.2",
              "assets": [
                { "name": "Sling-Setup.exe.sha256", "size": 90, "browser_download_url": "https://github.com/HendrikVrey/Sling/releases/download/v1.1.2/other" },
                {
                  "name": "{{name}}",
                  "size": {{Installer.Length}},
                  {{digestProperty}}
                  "browser_download_url": "{{url}}"
                }
              ]
            }
            """;
    }

    private static byte[] CreateInstaller()
    {
        var bytes = new byte[300_000];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage Bytes(byte[] bytes, bool declareLength = true)
    {
        HttpContent content = declareLength
            ? new ByteArrayContent(bytes)
            : new StreamContent(new UnknownLengthStream(bytes));

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    /// <summary>A reply whose connection drops after a few bytes, as HttpIOException does.</summary>
    private sealed class DroppingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent)
            {
                throw new IOException("The response ended prematurely.");
            }

            _sent = true;
            buffer[offset] = (byte)'{';
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that will not say how long it is, as a chunked download does not.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
