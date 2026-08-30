namespace Sling.Import.Postman;

/// <summary>
/// One JSON document handed to the importer, and the name it came from.
/// </summary>
/// <param name="Name">
/// The file name, used only in notes. It is never used to build an output path - output
/// paths come from the collection's own folder and request names, through
/// <see cref="FileNames"/>.
/// </param>
/// <param name="Json">The document's text.</param>
public sealed record PostmanSource(string Name, string Json);

/// <summary>
/// One file the import produced, ready to be written.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RelativePath"/> is always relative, always uses <c>/</c> as its separator,
/// and can only be built by <see cref="FileNames"/> - the constructor is internal and
/// every caller goes through the slug rules there. That is deliberate: the path is
/// assembled from folder and request <em>names inside somebody else's JSON file</em>, so
/// it is the one value in this project that turns untrusted input into a location on
/// disk.
/// </para>
/// <para>
/// The containment check in <c>Sling.Persistence</c> is the second line rather than the
/// first, the same shape as <c>CurlRequest.Note</c> sanitising while
/// <c>CurlImport.Write</c> comments every line anyway.
/// </para>
/// </remarks>
public sealed record ImportedFile
{
    internal ImportedFile(string relativePath, string text)
    {
        RelativePath = relativePath;
        Text = text;
    }

    /// <summary>Where the file goes, relative to the folder the user chose.</summary>
    public string RelativePath { get; }

    /// <summary>The file's whole content.</summary>
    public string Text { get; }
}

/// <summary>
/// The result of converting a Postman export.
/// </summary>
/// <param name="Files">
/// Every file to write: the <c>.http</c> documents, and the environment files when the
/// export carried variables.
/// </param>
/// <param name="Notes">
/// What the conversion could not do, at the level of the whole import. Notes about one
/// request are written into that request's document as comments instead, where the person
/// reading the request will actually meet them.
/// </param>
/// <param name="Recognized">
/// False when nothing handed in was a Postman export. <see cref="Files"/> is then empty
/// and the caller should say so rather than writing an empty folder.
/// </param>
public sealed record PostmanImportResult(
    IReadOnlyList<ImportedFile> Files,
    IReadOnlyList<string> Notes,
    bool Recognized)
{
    /// <summary>Nothing recognisable.</summary>
    public static PostmanImportResult NotPostman { get; } = new([], [], Recognized: false);

    /// <summary>Nothing recognisable, with a reason to show the user.</summary>
    internal static PostmanImportResult Unrecognized(IReadOnlyList<string> notes) =>
        new([], notes, Recognized: false);
}
