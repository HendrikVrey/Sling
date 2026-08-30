using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sling.Core.Auth;

namespace Sling.Persistence.Tokens;

/// <summary>
/// Access tokens that survive a restart, encrypted at rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>An accelerator, and it has to stay one.</b> Delete this store and nothing is lost but
/// a round trip to the token endpoint - the grant is still declared in the <c>.http</c> file
/// and the credential is still in the environment file, so the work runs unchanged. That is
/// the whole test: Sling may remember, and may not be the place the truth lives.
/// </para>
/// <para>
/// <b>Three things keep it from weakening the in-memory rules.</b>
/// </para>
/// <para>
/// It is encrypted with DPAPI under the current user, with the scope mixed into the
/// entropy - so the file is unreadable by another account, and a file copied from one
/// environment's slot to another's does not decrypt. The scoping that stops a staging token
/// reaching production is therefore enforced by the cryptography rather than by a file name.
/// </para>
/// <para>
/// It holds no client secret. Tokens are identified by
/// <see cref="TokenCacheKey.Fingerprint"/>, a hash over every field of the grant, so
/// rotating a secret stops the stored token matching at once and the secret itself is never
/// written down - not even inside the encrypted blob.
/// </para>
/// <para>
/// It is scoped per workspace and environment, exactly as the in-memory cache is, and it is
/// never consulted across that boundary because the scope decides both the file and the key.
/// </para>
/// <para>
/// Nothing here throws for a store that cannot be read. A token cache that fails to load is
/// a round trip, not an error, and refusing to open a workspace over one would be the tail
/// wagging the dog.
/// </para>
/// </remarks>
public sealed class TokenStore
{
    /// <summary>
    /// Mixed into the DPAPI entropy along with the scope.
    /// </summary>
    /// <remarks>
    /// Not a secret and not pretending to be one - DPAPI's protection comes from the user's
    /// own key material. Entropy binds a blob to the thing that wrote it, so a store written
    /// under one scope cannot be decrypted under another even by the same account, which is
    /// what makes the environment scoping structural rather than a naming convention.
    /// </remarks>
    private const string EntropyPrefix = "Sling.TokenStore.v1";

    /// <summary>The largest store that will be read, and roughly a thousand tokens.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Whether this machine can protect a token at rest at all.
    /// </summary>
    /// <remarks>
    /// DPAPI is Windows', and this project targets <c>net10.0</c> rather than
    /// <c>net10.0-windows</c> so that it can be tested without one. Somewhere else,
    /// remembering is simply off - which is the right answer and the only honest one. Writing
    /// a token in the clear because the encryption was unavailable is the failure mode this
    /// guard exists to make impossible.
    /// </remarks>
    [SupportedOSPlatformGuard("windows")]
    private static bool CanProtect => OperatingSystem.IsWindows();

    /// <param name="folder">
    /// Where to keep the stores. Taken rather than read from the environment, matching
    /// <see cref="Settings.SettingsStore"/>, so a test can point it at a disposable directory
    /// instead of at the profile of whoever is running it.
    /// </param>
    public TokenStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = Path.Combine(folder, "tokens");
    }

    /// <summary>Where the stores live, under the same local-data folder everything else uses.</summary>
    public string Folder { get; }

    /// <summary>
    /// The slot one workspace-and-environment pair writes to.
    /// </summary>
    /// <remarks>
    /// Hashed rather than spelled out. The path would otherwise carry a workspace's location
    /// in a file name, and somebody's directory tree is not a thing to leave lying around in
    /// <c>%LOCALAPPDATA%</c> for every process on the machine to enumerate.
    /// </remarks>
    public static string ScopeOf(string? workspaceRoot, string? environment)
    {
        var joined = string.Join(
            '\0',
            workspaceRoot?.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant() ?? string.Empty,
            environment ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    /// <summary>Reads the tokens stored for <paramref name="scope"/>.</summary>
    /// <remarks>
    /// Every failure answers with nothing: a store from another user, a store whose entropy
    /// no longer matches, a truncated file, a machine whose DPAPI keys have been reset. All
    /// of them mean the same thing to the caller, which is that a token has to be fetched.
    /// </remarks>
    public IReadOnlyList<PersistedToken> Load(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var path = PathFor(scope);

        try
        {
            if (!CanProtect || !File.Exists(path) || new FileInfo(path).Length > MaxBytes)
            {
                return [];
            }

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                Entropy(scope),
                DataProtectionScope.CurrentUser);

            return Read(plain);
        }
        catch (CryptographicException)
        {
            // Another user's blob, another scope's entropy, or a machine whose keys have
            // changed. Indistinguishable from each other and all equally uninteresting.
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces what is stored for <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// An empty list deletes the file rather than writing an empty one. A store with nothing
    /// in it is a file whose existence says a token was once here, and there is no reason to
    /// leave one behind.
    /// </remarks>
    public void Save(string scope, IReadOnlyList<PersistedToken> tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.Count == 0)
        {
            Clear(scope);
            return;
        }

        if (!CanProtect)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Folder);

            var protectedBytes = ProtectedData.Protect(
                Write(tokens),
                Entropy(scope),
                DataProtectionScope.CurrentUser);

            // Written to a sibling and moved over the target, so a crash leaves the previous
            // store rather than half of this one - the same shape as every other write here.
            var path = PathFor(scope);
            var temporary = path + ".sling-tmp";

            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (CryptographicException)
        {
            // Nothing to do and nothing lost: the tokens are still in memory and the next
            // start fetches new ones.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes the store for one scope.</summary>
    public void Clear(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        Delete(PathFor(scope));
    }

    /// <summary>
    /// Removes every store, for switching the whole feature off.
    /// </summary>
    /// <remarks>
    /// Turning off "remember tokens" has to take the ones already remembered with it.
    /// Leaving them would mean a setting that stops adding to a pile of credentials without
    /// removing the pile, which is not what anybody switching it off is asking for.
    /// </remarks>
    public void ClearAll()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string scope) => Path.Combine(Folder, scope + ".bin");

    private static byte[] Entropy(string scope) => Encoding.UTF8.GetBytes(EntropyPrefix + '\0' + scope);

    /// <summary>
    /// Writes the store's JSON.
    /// </summary>
    /// <remarks>
    /// Written by hand with <see cref="Utf8JsonWriter"/> rather than serialised, for the
    /// reason every other file in this project is: no source generation, so the project stays
    /// AOT-compatible, and no reflection over a type that holds a bearer token.
    /// </remarks>
    private static byte[] Write(IReadOnlyList<PersistedToken> tokens)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteStartArray("tokens");

            foreach (var token in tokens)
            {
                json.WriteStartObject();
                json.WriteString("fingerprint", token.Fingerprint);
                json.WriteString("tokenUrl", token.Identity.TokenUrl);
                json.WriteString("clientId", token.Identity.ClientId);

                if (token.Identity.Scope is { } scope)
                {
                    json.WriteString("scope", scope);
                }

                if (token.Identity.Audience is { } audience)
                {
                    json.WriteString("audience", audience);
                }

                json.WriteString("accessToken", token.AccessToken);
                json.WriteString("tokenType", token.TokenType);
                json.WriteString("expires", token.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture));
                json.WriteString("fetched", token.FetchedUtc.ToString("O", CultureInfo.InvariantCulture));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the store's JSON, skipping anything malformed.
    /// </summary>
    /// <remarks>
    /// Walked by hand, and an entry that does not read is dropped rather than taken as a
    /// reason to discard the file. The cost of dropping one is a round trip.
    /// </remarks>
    private static List<PersistedToken> Read(byte[] plain)
    {
        var tokens = new List<PersistedToken>();

        try
        {
            using var document = JsonDocument.Parse(plain);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tokens", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (TryReadToken(entry) is { } token)
                {
                    tokens.Add(token);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return tokens;
    }

    private static PersistedToken? TryReadToken(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || Text(entry, "fingerprint") is not { } fingerprint
            || Text(entry, "tokenUrl") is not { } tokenUrl
            || Text(entry, "clientId") is not { } clientId
            || Text(entry, "accessToken") is not { } accessToken
            || Text(entry, "tokenType") is not { } tokenType
            || !When(entry, "expires", out var expires)
            || !When(entry, "fetched", out var fetched))
        {
            return null;
        }

        return new PersistedToken(
            fingerprint,
            new TokenIdentity(tokenUrl, clientId, Text(entry, "scope"), Text(entry, "audience")),
            accessToken,
            tokenType,
            expires,
            fetched);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool When(JsonElement entry, string name, out DateTimeOffset value)
    {
        value = default;

        return Text(entry, name) is { } text
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
    }
}
