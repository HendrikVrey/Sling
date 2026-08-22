using System.Diagnostics.CodeAnalysis;

namespace Sling.Core.Variables;

/// <summary>
/// Supplies the bytes behind a <c>&lt; ./file</c> body import, so <c>Sling.Core</c> can
/// assemble a body without touching the disk.
/// </summary>
/// <remarks>
/// <para>
/// The implementation is also the enforcement point for <em>which</em> files a document
/// may read, and that is a security boundary rather than a convenience. A <c>.http</c>
/// file is something people share, paste from a colleague, or generate from an imported
/// Postman collection (<c>Sling.md</c> §5.8) — so <c>&lt; C:\Users\me\.ssh\id_rsa</c>
/// followed by a <c>POST</c> to an attacker's host is a request document, not an exotic
/// attack. Containment lives behind this interface precisely so it cannot be forgotten at
/// a call site.
/// </para>
/// <para>
/// Failures come back as text rather than exceptions. A missing file is an ordinary state
/// for a document that is being edited, and it belongs in the diagnostics list beside
/// every other thing that stops a request being sent.
/// </para>
/// </remarks>
public interface IRequestFileSource
{
    /// <summary>
    /// Reads the file named by <paramref name="path"/> as written in the document.
    /// </summary>
    /// <param name="path">The path exactly as the document wrote it, variables already substituted.</param>
    /// <param name="bytes">The file's contents, unaltered.</param>
    /// <param name="reason">
    /// Why it could not be read, phrased for the user. Never contains anything read from
    /// the file — only the path, which the document already shows.
    /// </param>
    bool TryRead(string path, [NotNullWhen(true)] out byte[]? bytes, [NotNullWhen(false)] out string? reason);
}

/// <summary>
/// The source used before a document has been saved. Refuses every import, and says why:
/// a relative path has nothing to be relative to until the document is a file.
/// </summary>
public sealed class NoRequestFiles : IRequestFileSource
{
    public static NoRequestFiles Instance { get; } = new();

    public bool TryRead(string path, [NotNullWhen(true)] out byte[]? bytes, [NotNullWhen(false)] out string? reason)
    {
        bytes = null;
        reason = "this document has not been saved yet, and a body import is resolved relative "
            + "to the file it is written in. Save it somewhere first";
        return false;
    }
}
