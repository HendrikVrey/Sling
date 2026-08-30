using System.Globalization;
using System.Text;
using Sling.Core.Documents;
using Sling.Core.Json;
using Sling.Core.Parsing;

namespace Sling.Core.Variables;

/// <summary>Which field a value is being substituted into, and therefore what it may contain.</summary>
internal enum FieldKind
{
    Target,
    HeaderName,
    HeaderValue,
    Body,
}

/// <summary>
/// Substitutes <c>{{references}}</c> into one field of one request, resolving file
/// variables and chained response values, and refusing any value that could change the
/// shape of the request rather than fill in a blank.
/// </summary>
/// <remarks>
/// <para>
/// <c>Sling.md</c> §5.7 - <em>chained values are data, never code</em> - is enforced
/// here and only here. Three things make it hold.
/// </para>
/// <para>
/// First, the request is already parsed into method, target and header fields before
/// this class runs, so a value has nowhere to go except inside the one field that
/// referenced it.
/// </para>
/// <para>
/// Second, every substituted value is checked against that field's character rules, so a
/// token containing a newline cannot smuggle a header in behind the one it was asked for.
/// </para>
/// <para>
/// Third - and this is the part that took a review to find - a value read from a
/// <em>response</em> is percent-encoded when it lands in a URL. Character rules alone are
/// not enough there: <c>@</c>, <c>:</c> and a backslash are all legal URL characters, and
/// <see cref="Uri"/> parses the authority after the check runs, so a value of
/// <c>@evil.example.com</c> substituted after a host silently retargets the whole request
/// - with the <c>Authorization</c> header attached, before any redirect policy can apply.
/// Encoding makes the value data rather than syntax, which is the only form of the rule
/// that does not depend on remembering which characters matter.
/// </para>
/// <para>
/// Encoding applies to response values only. The literal text of the document, the file
/// variables the user wrote, and the environment they selected are deliberately
/// <em>not</em> encoded and not checked: a request someone typed by hand is theirs to get
/// wrong, and <c>@base</c> holding <c>https://api.example.com</c> is the format's central
/// idiom - as is an environment supplying that same value per deployment.
/// </para>
/// </remarks>
internal sealed class VariableExpander
{
    /// <summary>
    /// Guards against a variable defined in terms of itself. The bound is on nesting
    /// depth as well, because two variables can reference each other through a chain
    /// long enough to be tedious to trace.
    /// </summary>
    private const int MaxNestingDepth = 32;

    /// <summary>
    /// The cap on what a request line or a header may expand to.
    /// </summary>
    /// <remarks>
    /// Depth alone does not bound this. <c>@v0 = xxxxxxxx</c> followed by
    /// <c>@vN = {{vN-1}}{{vN-1}}</c> doubles per level, so thirty legal levels reach
    /// several gigabytes - a hang and then an out-of-memory, from a document that looks
    /// like nothing. A megabyte is far past any real request target or header.
    /// </remarks>
    private const int MaxExpandedHeaderChars = 1024 * 1024;

    /// <summary>
    /// The cap on what a body may expand to.
    /// </summary>
    /// <remarks>
    /// A separate, much larger number, because a body is not a header and applying the
    /// header's cap to one made ordinary payloads fail. A 3 MB fixture imported with
    /// <c>&lt;@</c> was refused - while the same fixture imported with <c>&lt;</c> went
    /// through, and <c>WorkspaceFileSource</c> advertises 32 MB - and the message blamed a
    /// doubling variable the document did not contain. The failure even depended on
    /// whether the body happened to hold a <c>{{reference}}</c> at all.
    /// <para>
    /// The anti-amplification property is unaffected: the bound still exists, it is simply
    /// no longer 32× tighter than the caps sitting behind it.
    /// </para>
    /// </remarks>
    private const int MaxExpandedBodyChars = 64 * 1024 * 1024;

    private readonly Dictionary<string, VariableDefinition> _variables;
    private readonly IResponseLookup _responses;
    private readonly IVariableSource _environment;
    private readonly HashSet<string> _expanding = new(StringComparer.Ordinal);
    private readonly List<ParseDiagnostic> _errors = [];
    private readonly List<string> _missing = [];

    private bool _budgetReported;

    public VariableExpander(IReadOnlyList<VariableDefinition> variables, ResolutionContext context)
    {
        _responses = context.Responses;
        _environment = context.Environment;

        // Last definition wins, matching how the reference dialect treats a redefinition
        // further down the file.
        _variables = new Dictionary<string, VariableDefinition>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            _variables[variable.Name] = variable;
        }
    }

    public IReadOnlyList<ParseDiagnostic> Errors => _errors;

    /// <summary>Named requests that must be sent before this one can be resolved.</summary>
    public IReadOnlyList<string> MissingResponses => _missing;

    public string Expand(string template, int line, FieldKind field) => Expand(template, line, field, depth: 0);

    private string Expand(string template, int line, FieldKind field, int depth)
    {
        var opening = template.IndexOf("{{", StringComparison.Ordinal);
        if (opening < 0)
        {
            return template;
        }

        var result = new StringBuilder(template.Length);
        var position = 0;

        while (opening >= 0)
        {
            var closing = template.IndexOf("}}", opening + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                _errors.Add(ParseDiagnostic.Error("'{{' is never closed - a reference is written '{{name}}'.", line));
                break;
            }

            result.Append(template, position, opening - position);

            var reference = template[(opening + 2)..closing].Trim();
            if (TryResolve(reference, line, field, depth, out var value))
            {
                result.Append(value);
            }

            if (result.Length > BudgetFor(field))
            {
                ReportBudget(line, field);
                return string.Empty;
            }

            position = closing + 2;
            opening = template.IndexOf("{{", position, StringComparison.Ordinal);
        }

        result.Append(template, position, template.Length - position);
        return result.ToString();
    }

    private static int BudgetFor(FieldKind field) =>
        field == FieldKind.Body ? MaxExpandedBodyChars : MaxExpandedHeaderChars;

    private void ReportBudget(int line, FieldKind field)
    {
        if (_budgetReported)
        {
            return;
        }

        _budgetReported = true;

        var megabytes = BudgetFor(field) / (1024 * 1024);

        _errors.Add(ParseDiagnostic.Error(
            $"Substitution produced more than {megabytes.ToString(CultureInfo.InvariantCulture)} MB "
                + "of text. A variable that references two copies of another one doubles in size "
                + "at every level, which reaches gigabytes long before it reaches the nesting limit.",
            line));
    }

    private bool TryResolve(string reference, int line, FieldKind field, int depth, out string value)
    {
        value = string.Empty;

        if (_budgetReported)
        {
            return false;
        }

        if (reference.Length == 0)
        {
            _errors.Add(ParseDiagnostic.Error("'{{}}' names nothing.", line));
            return false;
        }

        if (depth >= MaxNestingDepth)
        {
            _errors.Add(ParseDiagnostic.Error(
                $"'{reference}' is nested more than {MaxNestingDepth} variables deep. "
                    + "That is almost always a definition that refers back to itself.",
                line));
            return false;
        }

        if (ChainReference.TryParse(reference, out var chain))
        {
            return TryResolveChain(chain, line, field, out value);
        }

        // The environment is consulted before the document's own variables, and that
        // ordering is the whole point of having environments (Sling.md §4c). The obvious
        // alternative - the file wins, as it does in the reference dialect - means a
        // document containing '@base = https://api.example.com' cannot be pointed at
        // staging without editing the line the environment exists to replace. Recorded as
        // a deliberate divergence in docs/http-dialect.md.
        if (_environment.TryGet(reference, out var fromEnvironment))
        {
            return TryExpandDefinition(reference, fromEnvironment, line, line, field, depth, out value);
        }

        if (!_variables.TryGetValue(reference, out var definition))
        {
            _errors.Add(ParseDiagnostic.Error(
                $"There is no variable named '{reference}'. Define it with '@{reference} = ...', "
                    + "put it in the selected environment, or reference an earlier request as "
                    + "'{{name.response.body.$.field}}'.",
                line));
            return false;
        }

        return TryExpandDefinition(reference, definition.Value, definition.Line, line, field, depth, out value);
    }

    /// <summary>
    /// Expands the text a name is bound to, wherever it was bound.
    /// </summary>
    /// <param name="definitionLine">
    /// The line to report a self-reference against. An environment value has no line in
    /// the document, so for one of those it is the line that referenced it - which is the
    /// only line the user can be shown.
    /// </param>
    private bool TryExpandDefinition(
        string reference,
        string definitionText,
        int definitionLine,
        int line,
        FieldKind field,
        int depth,
        out string value)
    {
        value = string.Empty;

        // One set covers both sources because a name resolves to at most one of them:
        // the environment shadows the file, so there is no cycle that alternates.
        if (!_expanding.Add(reference))
        {
            _errors.Add(ParseDiagnostic.Error(
                $"'{reference}' is defined in terms of itself.",
                definitionLine));
            return false;
        }

        try
        {
            // The definition's own text is expanded against the same field, because that
            // is where its value is about to land. Any response value inside it was
            // already encoded by the recursive call, so this level only re-checks
            // characters - encoding here as well would double-encode it.
            var expanded = Expand(definitionText, definitionLine, field, depth + 1);
            return Accept(expanded, reference, line, field, encode: false, out value);
        }
        finally
        {
            _expanding.Remove(reference);
        }
    }

    private bool TryResolveChain(ChainReference chain, int line, FieldKind field, out string value)
    {
        value = string.Empty;

        var response = _responses.Find(chain.RequestName);
        if (response is null)
        {
            if (!_missing.Contains(chain.RequestName, StringComparer.Ordinal))
            {
                _missing.Add(chain.RequestName);
            }

            return false;
        }

        if (chain.Part == ChainPart.Header)
        {
            var header = response.Header(chain.Path);
            if (header is null)
            {
                _errors.Add(ParseDiagnostic.Error(
                    $"'{chain}' found no '{chain.Path}' header on the response from '{chain.RequestName}'.",
                    line));
                return false;
            }

            return Accept(header, chain.ToString(), line, field, encode: true, out value);
        }

        if (response.BodyTruncated)
        {
            _errors.Add(ParseDiagnostic.Error(
                $"The response from '{chain.RequestName}' was too large to keep in full, so "
                    + $"'{chain}' cannot be read from it.",
                line));
            return false;
        }

        if (!JsonPathReader.TryRead(response.Body, chain.Path, out var extracted, out var error))
        {
            _errors.Add(ParseDiagnostic.Error($"'{chain}' could not be read: {error}.", line));
            return false;
        }

        return Accept(extracted, chain.ToString(), line, field, encode: true, out value);
    }

    /// <summary>
    /// The gate every substituted value passes through.
    /// </summary>
    /// <param name="encode">
    /// True when the value came straight from a response. Such a value is percent-encoded
    /// on its way into a URL, which is what stops it supplying authority syntax rather
    /// than a path or query component.
    /// </param>
    /// <remarks>
    /// A value that cannot legally sit in this field is rejected outright rather than
    /// escaped, because there is no escape mechanism in a request line or a header field
    /// to escape it into. The URL is the exception, and only because it has one.
    /// </remarks>
    private bool Accept(
        string candidate,
        string reference,
        int line,
        FieldKind field,
        bool encode,
        out string value)
    {
        // A body has no structure to break out of: it is a byte sequence terminated by a
        // length, not by a delimiter the value could contain.
        if (field == FieldKind.Body)
        {
            value = candidate;
            return true;
        }

        value = string.Empty;

        if (field == FieldKind.Target && encode)
        {
            value = Uri.EscapeDataString(candidate);
            return true;
        }

        var (isLegal, what) = field switch
        {
            FieldKind.Target => ((Func<char, bool>)HttpSyntax.IsLegalRequestTargetChar, "a URL"),
            FieldKind.HeaderName => (HttpSyntax.IsTokenChar, "a header name"),
            _ => (HttpSyntax.IsLegalHeaderValueChar, "a header value"),
        };

        if (candidate.All(isLegal))
        {
            value = candidate;
            return true;
        }

        // The message names the reference and the code point, never the value: the value
        // is quite often a bearer token.
        _errors.Add(ParseDiagnostic.Error(
            $"The value of '{reference}' contains {HttpSyntax.DescribeFirstIllegal(candidate, isLegal)}, "
                + $"which cannot appear in {what}. A value from a response is data and is never "
                + "allowed to alter the shape of a request.",
            line));

        return false;
    }
}
