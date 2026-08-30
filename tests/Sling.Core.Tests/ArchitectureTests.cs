using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sling.Core.Tests;

/// <summary>
/// Enforces the layering rules in <c>Sling.md</c> §3. These are the rules that keep
/// <c>Sling.Core</c> exhaustively testable without a GUI or a socket, and that keep
/// every network call auditable in one project.
/// </summary>
/// <remarks>
/// Deliberately checked against the project files and sources on disk rather than
/// against loaded assemblies. A compiled assembly only references what its IL actually
/// uses, so an assembly-level check silently passes while a project is still empty,
/// which is exactly when these rules are easiest to break and cheapest to fix. Reading
/// the csproj catches a forbidden <c>PackageReference</c> the moment it is added.
/// </remarks>
public sealed class ArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("Sling.Core")]
    [InlineData("Sling.Import")]
    public void Pure_projects_declare_no_package_reference(string project)
    {
        var packages = XDocument.Load(ProjectFile(project))
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .ToArray();

        Assert.True(
            packages.Length == 0,
            $"{project} must stay dependency-free so it can be unit-tested in isolation, "
                + $"but declares: {string.Join(", ", packages)}. If a dependency is genuinely "
                + "needed, it belongs in Sling.Http or Sling.Persistence.");
    }

    [Theory]
    [InlineData("Sling.Core")]
    [InlineData("Sling.Import")]
    [InlineData("Sling.Http")]
    [InlineData("Sling.Persistence")]
    public void Non_ui_projects_do_not_reference_wpf(string project)
    {
        AssertNoMatch(
            project,
            new Regex(@"\bSystem\.Windows\b|\bWpf\.Ui\b|\bICSharpCode\b", RegexOptions.Compiled),
            "UI belongs in Sling.App. A non-UI project that reaches for WPF cannot be "
                + "tested headlessly and drags a Windows-only target framework behind it.");
    }

    [Fact]
    public void Core_does_not_reach_for_the_network()
    {
        AssertNoMatch(
            "Sling.Core",
            new Regex(@"\bSystem\.Net\b|\bHttpClient\b", RegexOptions.Compiled),
            "Sling.Http is the only project that touches the network - that is what makes "
                + "the credential-stripping and TLS rules in Sling.md §5 auditable by reading "
                + "one project.");
    }

    /// <summary>
    /// <c>Sling.Import</c> is held to this as well as <c>Sling.Core</c>, and it has a reason
    /// of its own: an importer that can read files is an importer a hostile collection can
    /// aim at one. Being pure text-to-text is also what lets it be tested against a corpus.
    /// </summary>
    [Theory]
    [InlineData("Sling.Core")]
    [InlineData("Sling.Import")]
    public void Pure_projects_do_not_reach_for_the_disk(string project)
    {
        AssertNoMatch(
            project,
            new Regex(@"\b(File|Directory|FileStream|FileInfo)\s*\.", RegexOptions.Compiled),
            "Disk I/O belongs in Sling.Persistence. Keeping it there is what lets the "
                + "'secrets never land in a committed file' rule be checked by reading one "
                + "project.");
    }

    [Theory]
    [InlineData("Sling.Core")]
    [InlineData("Sling.Import")]
    [InlineData("Sling.Http")]
    [InlineData("Sling.Persistence")]
    [InlineData("Sling.App")]
    public void No_project_renders_a_response_in_a_browser_control(string project)
    {
        // Sling.md §5.5. A response body is untrusted input from a system the user does
        // not control, so it renders as text; a WebBrowser or WebView2 control would
        // execute whatever is in it. Checked across every project rather than the UI one
        // alone - the rule is about the product, not about one assembly's dependencies.
        AssertNoMatch(
            project,
            new Regex(@"\bWebBrowser\b|\bWebView2?\b|\bCoreWebView2\b", RegexOptions.Compiled),
            "A response body renders as text, never into a control that can execute it.");
    }

    private static void AssertNoMatch(string project, Regex forbidden, string why)
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // Not named 'File' - Etch lost an afternoon to an x:Name that shadowed a type,
            // and System.IO.File is in scope right here.
            .Select(f => (Relative: Path.GetRelativePath(RepoRoot, f), Match: forbidden.Match(CodeOnly(File.ReadAllText(f)))))
            .Where(x => x.Match.Success)
            .Select(x => $"{x.Relative} ('{x.Match.Value}')")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{why}{Environment.NewLine}Offending files: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// Drops whole-line comments before matching, so a rule about code is not tripped by
    /// documentation that names the very thing it forbids.
    /// </summary>
    /// <remarks>
    /// Written after exactly that happened: the remark on <c>MainWindow.ShowText</c>
    /// explains that a response body never reaches a browser control, and naming the
    /// control is what makes the remark useful. A check that punishes a file for
    /// explaining itself teaches people to stop explaining.
    /// <para>
    /// Whole lines only, deliberately. A trailing comment on a line of code is still
    /// matched - the parsing needed to strip one safely (string literals, verbatim and
    /// raw strings, block comments) is more machinery than a grep should carry, and
    /// erring strict is the right side to err on here.
    /// </para>
    /// </remarks>
    private static string CodeOnly(string source) =>
        string.Join(
            '\n',
            source
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string ProjectFile(string project) =>
        Path.Combine(RepoRoot, "src", project, $"{project}.csproj");

    /// <summary>
    /// Walks up from the test binary until the solution file appears, so the tests do
    /// not depend on the depth of the output directory (which differs between a local
    /// build, a Release build, and CI).
    /// </summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sling.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate Sling.slnx above '{AppContext.BaseDirectory}'. "
                + "ArchitectureTests reads the repository from disk and cannot run without it.");
    }
}
