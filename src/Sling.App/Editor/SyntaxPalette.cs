using System.Windows;
using System.Windows.Media;

namespace Sling.App.Editor;

/// <summary>The kind of thing a grammar's colour marks.</summary>
internal enum SyntaxRole
{
    /// <summary>Not one of the roles Sling has an opinion about.</summary>
    Unmapped = 0,

    /// <summary>Comments of every kind, including doc comments.</summary>
    Comment,

    /// <summary>String and character literals.</summary>
    String,

    /// <summary>Language keywords.</summary>
    Keyword,

    /// <summary>Numeric literals, and the boolean and null constants beside them.</summary>
    Number,

    /// <summary>Brackets, operators and separators.</summary>
    Punctuation,

    /// <summary>Type names and the keywords that name types.</summary>
    Type,

    /// <summary>Method and function names.</summary>
    Function,

    /// <summary>Preprocessor and directive lines, and entity references.</summary>
    Preprocessor,

    /// <summary>Markup element names.</summary>
    Tag,

    /// <summary>Markup attribute names.</summary>
    Attribute,

    /// <summary>Something the grammar marked as wrong.</summary>
    Invalid,
}

/// <summary>
/// Sling's syntax colours, and the promise that every one of them is readable on the pane
/// it is drawn on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dark only, because Sling is dark only.</b> <c>App.xaml</c> merges one theme
/// dictionary and <c>App.OnStartup</c> applies it unconditionally; there is no theme
/// switch and no light palette to keep in step. If Sling ever grows one, this class grows
/// a second seed column and <see cref="ThemedHighlightingColorizer"/> grows the cache
/// invalidation to go with it — and at that point the whole file is worth extracting into
/// a package shared with Etch rather than being maintained twice. It is not worth
/// extracting for one consumer with one theme.
/// </para>
/// <para>
/// The seeds below are the VS Code Dark+ family, and the name-to-role table is the same
/// mapping Etch uses — necessarily, since both read the same AvalonEdit grammars and the
/// names in it come from those grammar files. What is <em>not</em> shared is the
/// machinery: Etch derives a light palette too and has to reconcile both, where this only
/// ever answers one question.
/// </para>
/// <para>
/// Nothing here is trusted to be legible on faith. <see cref="ForRole"/> runs every seed
/// through <see cref="Contrast.Legible"/> against the actual page colour, so the floor
/// holds even if a seed is edited carelessly or the pane's background changes.
/// </para>
/// </remarks>
internal static class SyntaxPalette
{
    /// <summary>
    /// The readability floor, in WCAG contrast ratio.
    /// </summary>
    /// <remarks>
    /// 4.5:1 — the AA requirement for body text, applied to code because code *is* the
    /// body text of this pane. The larger-text allowance of 3:1 does not apply to a 13 px
    /// monospace font.
    /// </remarks>
    internal const double MinimumContrast = 4.5;

    /// <summary>
    /// The page every ratio is measured against when the real one cannot be read.
    /// </summary>
    /// <remarks>
    /// WPF-UI's dark application background, alpha dropped. The panes are a translucent
    /// card over Mica, which has no statable contrast of its own — this is the opaque
    /// colour that surface is tinted towards and the closest honest stand-in.
    /// </remarks>
    internal static readonly Color FallbackPage = Color.FromRgb(0x20, 0x20, 0x20);

    /// <summary>The colour each role starts from, before the legibility clamp.</summary>
    /// <remarks>
    /// <see cref="SyntaxRole.Tag"/> is deliberately not the same blue as
    /// <see cref="SyntaxRole.Keyword"/>. They are borrowed from editors where no document
    /// shows markup and code at once — HTML does, and in Etch the two clamped to the same
    /// byte value, which a contrast test cannot see. <see cref="RolesAreDistinct"/> is the
    /// tripwire for that.
    /// </remarks>
    private static readonly (SyntaxRole Role, Color Seed)[] Seeds =
    [
        (SyntaxRole.Comment, Rgb(0x6A, 0x99, 0x55)),
        (SyntaxRole.String, Rgb(0xCE, 0x91, 0x78)),
        (SyntaxRole.Keyword, Rgb(0x56, 0x9C, 0xD6)),
        (SyntaxRole.Number, Rgb(0xB5, 0xCE, 0xA8)),
        (SyntaxRole.Punctuation, Rgb(0xC8, 0xC8, 0xC8)),
        (SyntaxRole.Type, Rgb(0x4E, 0xC9, 0xB0)),
        (SyntaxRole.Function, Rgb(0xDC, 0xDC, 0xAA)),
        (SyntaxRole.Preprocessor, Rgb(0xC5, 0x86, 0xC0)),
        (SyntaxRole.Tag, Rgb(0x7A, 0xB8, 0xF5)),
        (SyntaxRole.Attribute, Rgb(0x9C, 0xDC, 0xFE)),
        (SyntaxRole.Invalid, Rgb(0xF4, 0x87, 0x71)),
    ];

    /// <summary>
    /// Grammar colour names, as AvalonEdit's definition files declare them.
    /// </summary>
    /// <remarks>
    /// A name not in here is not an error — <see cref="Rescue"/> keeps the grammar's own
    /// colour and only drags it up to the floor, which is the right answer for the long
    /// tail.
    /// </remarks>
    private static readonly Dictionary<string, SyntaxRole> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Comment"] = SyntaxRole.Comment,
        ["DocComment"] = SyntaxRole.Comment,
        ["CommentTags"] = SyntaxRole.Comment,
        ["JavaDocTags"] = SyntaxRole.Comment,
        ["KnownDocTags"] = SyntaxRole.Comment,
        ["BlockQuote"] = SyntaxRole.Comment,

        ["String"] = SyntaxRole.String,
        ["XmlString"] = SyntaxRole.String,
        ["Char"] = SyntaxRole.String,
        ["Character"] = SyntaxRole.String,
        ["Regex"] = SyntaxRole.String,
        ["StringInterpolation"] = SyntaxRole.String,
        ["Value"] = SyntaxRole.String,
        ["DateLiteral"] = SyntaxRole.String,

        ["Digits"] = SyntaxRole.Number,
        ["NumberLiteral"] = SyntaxRole.Number,
        ["Number"] = SyntaxRole.Number,
        ["Literals"] = SyntaxRole.Number,
        ["Constants"] = SyntaxRole.Number,
        ["BooleanConstants"] = SyntaxRole.Number,
        ["TrueFalse"] = SyntaxRole.Number,
        ["Bool"] = SyntaxRole.Number,
        ["Null"] = SyntaxRole.Number,
        ["NullOrValueKeywords"] = SyntaxRole.Number,

        ["Punctuation"] = SyntaxRole.Punctuation,
        ["XmlPunctuation"] = SyntaxRole.Punctuation,
        ["Operators"] = SyntaxRole.Punctuation,
        ["CurlyBraces"] = SyntaxRole.Punctuation,
        ["Colon"] = SyntaxRole.Punctuation,
        ["Slash"] = SyntaxRole.Punctuation,
        ["Assignment"] = SyntaxRole.Punctuation,

        ["ValueTypes"] = SyntaxRole.Type,
        ["ReferenceTypes"] = SyntaxRole.Type,
        ["ValueTypeKeywords"] = SyntaxRole.Type,
        ["ReferenceTypeKeywords"] = SyntaxRole.Type,
        ["TypeKeywords"] = SyntaxRole.Type,
        ["DataTypes"] = SyntaxRole.Type,
        ["OtherTypes"] = SyntaxRole.Type,
        ["Class"] = SyntaxRole.Type,
        ["Void"] = SyntaxRole.Type,

        ["MethodCall"] = SyntaxRole.Function,
        ["MethodName"] = SyntaxRole.Function,
        ["FunctionCall"] = SyntaxRole.Function,
        ["FunctionKeywords"] = SyntaxRole.Function,
        ["Command"] = SyntaxRole.Function,

        ["Preprocessor"] = SyntaxRole.Preprocessor,
        ["EntityReference"] = SyntaxRole.Preprocessor,
        ["Entities"] = SyntaxRole.Preprocessor,

        ["HtmlTag"] = SyntaxRole.Tag,
        ["Tags"] = SyntaxRole.Tag,
        ["ScriptTag"] = SyntaxRole.Tag,
        ["JavaScriptTag"] = SyntaxRole.Tag,
        ["JScriptTag"] = SyntaxRole.Tag,
        ["VBScriptTag"] = SyntaxRole.Tag,
        ["ASPSectionStartEndTags"] = SyntaxRole.Tag,

        ["Attributes"] = SyntaxRole.Attribute,
        ["Property"] = SyntaxRole.Attribute,
        ["Selector"] = SyntaxRole.Attribute,
        ["FieldName"] = SyntaxRole.Attribute,
        ["Variable"] = SyntaxRole.Attribute,

        ["UnknownScriptTag"] = SyntaxRole.Invalid,
        ["UnknownAttribute"] = SyntaxRole.Invalid,
        ["RemovedText"] = SyntaxRole.Invalid,
    };

    /// <summary>
    /// Suffixes that make a name a keyword whatever else it says.
    /// </summary>
    /// <remarks>
    /// Twenty-odd grammar colour names are some flavour of keyword —
    /// <c>GotoKeywords</c>, <c>ExceptionKeywords</c>, <c>ControlStatements</c> — and
    /// listing each would be an inventory that goes stale when a grammar is added. The
    /// suffixes are a rule about how these files are named, which is more durable.
    /// </remarks>
    private static readonly string[] KeywordSuffixes =
        ["Keywords", "Keyword", "Statements", "Modifiers", "Visibility", "ControlFlow"];

    /// <summary>Every role that has a colour, for the palette tests.</summary>
    internal static IEnumerable<SyntaxRole> Roles => Seeds.Select(static seed => seed.Role);

    /// <summary>
    /// The opaque colour to measure contrast against, read from the live theme when it can
    /// be.
    /// </summary>
    /// <param name="resources">
    /// The application's resource dictionary, or null in a test or before startup.
    /// </param>
    /// <remarks>
    /// Read rather than hardcoded so that the promise survives a theme dictionary update,
    /// but with a constant behind it because a missing resource must degrade to a
    /// plausible page rather than to <c>Colors.Transparent</c> — against which everything
    /// clears every floor, and the whole guarantee silently evaporates.
    /// </remarks>
    internal static Color Page(ResourceDictionary? resources)
    {
        if (resources?["ApplicationBackgroundColor"] is Color colour && colour.A > 0)
        {
            return Color.FromRgb(colour.R, colour.G, colour.B);
        }

        return FallbackPage;
    }

    /// <summary>The role a grammar's colour name maps to.</summary>
    /// <param name="name">
    /// The colour's name, which is null for the many rules that specify a colour inline
    /// rather than referring to a named one.
    /// </param>
    internal static SyntaxRole RoleOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return SyntaxRole.Unmapped;
        }

        if (ByName.TryGetValue(name, out var role))
        {
            return role;
        }

        foreach (var suffix in KeywordSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return SyntaxRole.Keyword;
            }
        }

        return SyntaxRole.Unmapped;
    }

    /// <summary>
    /// The colour to draw <paramref name="role"/> in, or null to keep what the grammar
    /// said.
    /// </summary>
    internal static Color? ForRole(SyntaxRole role, Color page)
    {
        foreach (var seed in Seeds)
        {
            if (seed.Role == role)
            {
                return Contrast.Legible(seed.Seed, page, MinimumContrast);
            }
        }

        return null;
    }

    /// <summary>Keeps a grammar's own colour but drags it up to the readability floor.</summary>
    internal static Color Rescue(Color original, Color page) =>
        Contrast.Legible(original, page, MinimumContrast);

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);
}
