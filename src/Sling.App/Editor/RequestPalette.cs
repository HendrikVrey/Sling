using System.Windows;
using System.Windows.Media;
using Sling.App.Collections;
using Sling.Core.Parsing;

namespace Sling.App.Editor;

/// <summary>
/// The colour every part of a <c>.http</c> document is drawn in, and the promise that each
/// one is readable on the pane it is drawn on.
/// </summary>
/// <remarks>
/// <para>
/// A second palette rather than more entries in <see cref="SyntaxPalette"/>, because the two
/// answer different questions. <see cref="SyntaxPalette"/> translates <em>somebody else's</em>
/// grammar colours - AvalonEdit's, whose names come from files Sling does not own - into
/// this application's family. This one assigns a colour to a token kind Sling defines
/// itself, and it can therefore be a table rather than a name-matching exercise.
/// </para>
/// <para>
/// <b>It borrows the seeds rather than inventing them.</b> Every colour below comes from
/// <see cref="SyntaxPalette"/> or <see cref="MethodPalette"/> except one, so a response body
/// and the request that produced it are drawn in the same family and the two panes read as
/// one product. The exception is <see cref="HttpTokenKind.Reference"/>, and it earns a seed
/// of its own: a <c>{{reference}}</c> is the single most common thing to get wrong in this
/// format, it can appear inside a target, a header value, a variable and a body, and sharing
/// a colour with any of those would hide it in exactly the places it matters.
/// </para>
/// <para>
/// Dark only, because Sling is dark only. The note on <see cref="SyntaxPalette"/> covers what
/// changes if that stops being true.
/// </para>
/// </remarks>
internal static class RequestPalette
{
    /// <summary>
    /// The readability floor, in WCAG contrast ratio. The same 4.5:1 the rest of the
    /// application holds itself to; the request pane is body text like any other.
    /// </summary>
    internal const double MinimumContrast = SyntaxPalette.MinimumContrast;

    /// <summary>
    /// The <c>{{reference}}</c> colour, and the only seed here that is not borrowed.
    /// </summary>
    /// <remarks>
    /// A violet, chosen for where it is <em>not</em>: away from the value orange it sits
    /// inside, away from the six verb colours it sits beside on a request line, and away
    /// from the blue of a version and the light blue of a header name.
    /// <see cref="RequestPaletteTests"/> is the tripwire that keeps it there.
    /// </remarks>
    private static readonly Color ReferenceSeed = Color.FromRgb(0xA7, 0x8B, 0xFA);

    /// <summary>
    /// Which of <see cref="SyntaxPalette"/>'s roles each token kind borrows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four kinds share the string colour - a directive's argument, a request target, a
    /// header value and an import's path - and that is the point rather than an oversight.
    /// They are one role: the literal text somebody typed, as opposed to the name of the
    /// thing it is being given to. None of the four can appear on the same line as another.
    /// </para>
    /// <para>
    /// <see cref="HttpTokenKind.Method"/> and <see cref="HttpTokenKind.Title"/> are absent
    /// because neither takes a fixed colour: a verb is coloured by
    /// <see cref="MethodPalette"/> so the editor and the collections rail agree, and a
    /// separator's title is the pane's own text colour, because a heading is not a syntax
    /// element with a hue - it is the strongest text on the page.
    /// </para>
    /// </remarks>
    private static readonly (HttpTokenKind Kind, SyntaxRole Role)[] Roles =
    [
        (HttpTokenKind.Comment, SyntaxRole.Comment),
        (HttpTokenKind.Directive, SyntaxRole.Preprocessor),
        (HttpTokenKind.DirectiveValue, SyntaxRole.String),
        (HttpTokenKind.VariableName, SyntaxRole.Attribute),
        (HttpTokenKind.Operator, SyntaxRole.Punctuation),
        (HttpTokenKind.Target, SyntaxRole.String),
        (HttpTokenKind.Version, SyntaxRole.Keyword),
        (HttpTokenKind.HeaderName, SyntaxRole.Attribute),
        (HttpTokenKind.HeaderValue, SyntaxRole.String),
        (HttpTokenKind.ImportMarker, SyntaxRole.Keyword),
        (HttpTokenKind.ImportPath, SyntaxRole.String),
    ];

    /// <summary>
    /// The kinds drawn bold, and the reason each one is: a verb and a heading are what
    /// somebody scans a request file for, and an import marker is the one character that
    /// turns a line of text into a file read off disk.
    /// </summary>
    private static readonly HashSet<HttpTokenKind> Bold =
    [
        HttpTokenKind.Method,
        HttpTokenKind.Title,
        HttpTokenKind.ImportMarker,
    ];

    /// <summary>Whether <paramref name="kind"/> is drawn in a heavier weight.</summary>
    internal static bool IsBold(HttpTokenKind kind) => Bold.Contains(kind);

    /// <summary>
    /// The colour a token kind is drawn in, or null when the kind takes its colour from
    /// somewhere else.
    /// </summary>
    /// <param name="kind">The token kind.</param>
    /// <param name="page">The opaque colour every ratio is measured against.</param>
    /// <param name="text">
    /// The pane's own body-text colour, which a separator's title is drawn in.
    /// </param>
    /// <returns>
    /// Null for <see cref="HttpTokenKind.Method"/> only: a verb's colour depends on which
    /// verb it is, and <see cref="MethodPalette"/> owns that.
    /// </returns>
    internal static Color? For(HttpTokenKind kind, Color page, Color text)
    {
        if (kind == HttpTokenKind.Method)
        {
            return null;
        }

        if (kind == HttpTokenKind.Title)
        {
            return text;
        }

        if (kind == HttpTokenKind.Reference)
        {
            return Contrast.Legible(ReferenceSeed, page, MinimumContrast);
        }

        foreach (var (candidate, role) in Roles)
        {
            if (candidate == kind)
            {
                return SyntaxPalette.ForRole(role, page);
            }
        }

        return null;
    }

    /// <summary>
    /// A frozen brush per token kind, built once against <paramref name="page"/>.
    /// </summary>
    /// <remarks>
    /// Frozen for the reason <see cref="MethodPalette"/>'s are: these are handed to a line
    /// transformer that runs on every redraw, and an unfrozen brush owned by one thread is
    /// how that becomes an exception nobody can reproduce.
    /// </remarks>
    internal static IReadOnlyDictionary<HttpTokenKind, Brush> Build(ResourceDictionary? resources)
    {
        var page = SyntaxPalette.Page(resources);
        var text = SyntaxPalette.Text(resources);

        var brushes = new Dictionary<HttpTokenKind, Brush>();

        foreach (var kind in Enum.GetValues<HttpTokenKind>())
        {
            if (For(kind, page, text) is not { } colour)
            {
                continue;
            }

            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            brushes[kind] = brush;
        }

        return brushes;
    }
}
