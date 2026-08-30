using System.Globalization;
using Sling.Core.Parsing;

namespace Sling.Import.Postman;

/// <summary>
/// Writes one Postman request into a <c>.http</c> document.
/// </summary>
/// <remarks>
/// Everything that can produce a note runs before anything is written, so the comments
/// explaining a request all sit above it rather than being scattered through it. That
/// ordering is also what lets the <c># @auth</c> directives land immediately above the
/// request line, where they belong.
/// </remarks>
internal static class RequestConverter
{
    /// <param name="inherited">
    /// The auth in force from the enclosing folder or the collection. Overridden by
    /// anything the request or its item declares, including an explicit
    /// <c>{ "type": "noauth" }</c>, which is a real answer and not a way of saying
    /// "inherit".
    /// </param>
    public static void Write(
        PostmanItem item,
        PostmanRequest request,
        PostmanAuth? inherited,
        HttpWriter writer,
        ImportContext context)
    {
        writer.StartRequest(item.Name);

        writer.Comment(item.Description);

        // A request carries its own description as well as the item's, and they are usually
        // the same string. Written once - a doubled paragraph above every request reads like
        // a bug in the importer, which is not a good first impression of one.
        if (!string.Equals(request.Description, item.Description, StringComparison.Ordinal))
        {
            writer.Comment(request.Description);
        }

        foreach (var script in item.Scripts)
        {
            writer.Script(script.Kind, script.Source);
        }

        if (item.SavedResponses > 0)
        {
            writer.Note(
                $"Postman had {item.SavedResponses.ToString(CultureInfo.InvariantCulture)} saved "
                    + "example response"
                    + (item.SavedResponses == 1 ? string.Empty : "s")
                    + " for this request. Sling shows real responses only, so "
                    + (item.SavedResponses == 1 ? "it was" : "they were")
                    + " not imported.");
        }

        // The request's own block first, then the item's, then whatever was inherited. The
        // first two are both "this request's auth" - the schema puts a request's on the
        // request object and a folder's on the item - and a request item can legally carry
        // either, because the app has written both.
        var plan = AuthConverter.Convert(request.Auth ?? item.Auth ?? inherited, writer, context);
        var target = TargetBuilder.Build(request.Url, writer);
        var body = Sendable(BodyConverter.Convert(request.Body, writer), writer);

        if (target is null)
        {
            // No request line is written at all, which leaves the title and the notes as
            // documentation and leaves the document sendable. Writing a request line with an
            // empty target would produce a parse error on a request nobody can fix, because
            // the information needed to fix it was never in the export.
            writer.Note("This request has no URL in the collection, so there was nothing to import.");
            return;
        }

        foreach (var directive in plan.Directives)
        {
            writer.Directive(directive);
        }

        writer.RequestLine(Method(request, writer), Append(target, plan.QueryParameters));

        WriteHeaders(request, plan, body, writer);

        if (body is not null)
        {
            writer.Body(body.Text);
        }
    }

    /// <summary>
    /// Drops a body that would stop being a body once written down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A note is not a mitigation for structure injection, and treating it as one was a
    /// real hole.</b> <c>###</c> at the start of a line separates requests in this format,
    /// and nothing can escape it - so a body carrying one, written out with a comment above
    /// it saying so, produced a document that <em>parsed into extra requests</em>. A crafted
    /// collection used that to add a request named <c>login</c> and a second one carrying
    /// <c>{{login.response.body.$.token}}</c> to another host, and run-all would have sent
    /// both. The comment made it look handled.
    /// </para>
    /// <para>
    /// So the body is not written at all. It is reproduced as comments instead - the
    /// treatment a script gets, and for the same reason: the user needs to see it, and it
    /// must not be part of the document. Restoring it means putting it in a file and
    /// importing it with <c>&lt; ./file</c>, which is what the note says.
    /// </para>
    /// <para>
    /// The <c>Content-Type</c> header is still written, deliberately: it says what the body
    /// was meant to be, and a request that has lost its body is easier to repair with it
    /// than without.
    /// </para>
    /// </remarks>
    private static BodyPlan? Sendable(BodyPlan? body, HttpWriter writer)
    {
        if (body is null || !HttpWriter.WouldSplitTheDocument(body.Text))
        {
            return body;
        }

        writer.Excerpt(
            "The body contains a line starting with ###, which separates requests in a .http "
                + "file - so it CANNOT be written here without splitting this document, and it "
                + "has been left out. It is reproduced below. Put it in a file beside this one "
                + "and import it with '< ./file'.",
            body.Text);

        // Emptied rather than dropped, so the Content-Type the mode implies is still
        // written: it says what the body was meant to be, and a request that has lost its
        // body is easier to repair with it than without. An empty body writes nothing.
        return body with { Text = string.Empty };
    }

    /// <summary>
    /// The method, upper-cased, defaulting the way the format does.
    /// </summary>
    /// <remarks>
    /// A method that is not an RFC 9110 token is noted rather than repaired. Extension verbs
    /// are real and refusing them would be wrong; a method with a space in it is not an
    /// extension verb, and sending the first word of it silently would be a request the
    /// collection did not describe.
    /// </remarks>
    private static string Method(PostmanRequest request, HttpWriter writer)
    {
        var method = TextSafety.StripControl(request.Method ?? string.Empty).Trim();

        if (method.Length == 0)
        {
            return "GET";
        }

        if (!HttpSyntax.IsToken(method))
        {
            writer.Note(
                $"The method '{HttpWriter.Describe(method)}' is not a legal HTTP method, and the "
                    + "characters that are not have been dropped from the line below.");
        }

        return method;
    }

    /// <summary>Adds the query parameters an API-key auth block asked for.</summary>
    private static string Append(string target, IReadOnlyList<PostmanPair> query)
    {
        if (query.Count == 0)
        {
            return target;
        }

        // The fragment is stripped rather than kept behind the new parameters: '#' ends the
        // URL for the server, so a parameter appended after one is never sent, and a
        // credential that is silently not sent looks exactly like an API rejecting it.
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var withoutHash = hash < 0 ? target : target[..hash];

        var separator = withoutHash.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return withoutHash
            + separator
            + string.Join('&', query.Select(q => q.Key + "=" + (q.Value ?? string.Empty)));
    }

    /// <summary>
    /// Writes the headers: the collection's own, then whatever auth added, then the
    /// <c>Content-Type</c> the body implies.
    /// </summary>
    /// <remarks>
    /// An auth header never displaces one the collection wrote by hand. Postman resolves
    /// that collision in its own way and the export does not record which won, so the
    /// defensible answer is to keep what the author typed and say that the auth block also
    /// wanted that header - a silent choice either way would produce a request that
    /// authenticates differently from the one they were running.
    /// </remarks>
    private static void WriteHeaders(
        PostmanRequest request,
        AuthPlan plan,
        BodyPlan? body,
        HttpWriter writer)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            if (writer.Header(header.Key, header.Value))
            {
                written.Add(header.Key.Trim());
            }
            else
            {
                writer.Note(
                    $"A header called '{HttpWriter.Describe(header.Key)}' was dropped: that is not "
                        + "a name a header can have.");
            }
        }

        foreach (var header in plan.Headers)
        {
            if (!written.Add(header.Key))
            {
                writer.Note(
                    $"The collection's auth settings also wanted a '{HttpWriter.Describe(header.Key)}' "
                        + "header. The one written above came from the request itself and was kept.");

                continue;
            }

            writer.Header(header.Key, header.Value);
        }

        if (body?.ImpliedContentType is { } contentType && written.Add("Content-Type"))
        {
            writer.Header("Content-Type", contentType);
        }
    }
}
