using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Sling.App.Editor;
using Sling.Core.Auth;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// The auth panel: what credential the request under the caret sends, and changing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The panel is a view onto the document, exactly as the collections rail is a view onto
/// the folder.</b> Every field here writes headers and directives into the open
/// <c>.http</c> file; nothing about a request's auth is remembered anywhere else. Delete
/// Sling and the auth still works, because it is written in the file - which is the whole
/// difference from the tool whose auth tab writes into a database.
/// </para>
/// <para>
/// Answering "what credential is this request actually sending" used to mean reading three
/// files: the document, the committed environment file and the private one. The first two
/// lines of this card are that answer.
/// </para>
/// <para>
/// <b>A credential typed into it never reaches the document.</b> It goes to
/// <c>http-client.private.env.json</c> through the environment editor and the document gets
/// a <c>{{reference}}</c> - the rule the Postman importer already holds, which exists
/// because an imported document is meant to be committed and a pasted token is not.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The kinds the picker offers, in the order it offers them.</summary>
    /// <remarks>
    /// <see cref="AuthScheme.Unrecognized"/> is not here. It is a thing a document can say
    /// and not a thing this panel writes, so it appears only when the request already has
    /// one - and then as an entry that describes rather than an option to choose.
    /// </remarks>
    private static readonly (AuthScheme Scheme, string Label)[] AuthChoices =
    [
        (AuthScheme.None, "No auth"),
        (AuthScheme.Bearer, "Bearer token"),
        (AuthScheme.Basic, "Basic"),
        (AuthScheme.ApiKeyHeader, "API key in a header"),
        (AuthScheme.ClientCredentials, "OAuth2 client credentials"),
    ];

    private const string UnrecognizedLabel = "Something else (left as written)";

    private const string BasicHeaderLabel = "an Authorization: Basic header (default)";
    private const string FormBodyLabel = "the form body";

    /// <summary>The request the panel is describing, as it was parsed when the card opened.</summary>
    /// <remarks>
    /// Snapshotted rather than re-read on apply. The document behind the scrim cannot be
    /// typed into while the card is up, and holding the block is what lets the edit be
    /// computed against the same text the panel was filled from - the one thing every offset
    /// in it depends on.
    /// </remarks>
    private RequestBlock? _authBlock;

    /// <summary>The text those offsets belong to.</summary>
    private string _authText = string.Empty;

    /// <summary>The variable the current auth resolves from, when it is exactly one.</summary>
    private string? _authVariable;

    /// <summary>Guards the field refresh against the control changes it makes itself.</summary>
    private bool _updatingAuthForm;

    private bool AuthIsOpen => AuthOverlay.Visibility == Visibility.Visible;

    private void OnAuthClicked(object sender, RoutedEventArgs e) => ShowAuth();

    private void OnCloseAuth(object sender, RoutedEventArgs e) => CloseAuth();

    /// <summary>Opens the panel on the request the caret is in.</summary>
    private void ShowAuth()
    {
        var text = RequestPane.Text;
        var document = RequestDocumentParser.Parse(text);
        var block = document.BlockAtLine(RequestPane.TextArea.Caret.Line);

        if (block is null)
        {
            StatusLeft.Text = "There is no request here yet. Write a method and a URL, then try again.";
            return;
        }

        _authText = text;
        _authBlock = block;

        var view = RequestAuth.Describe(block);
        _authVariable = view.Variable;

        FillAuthCard(view);

        Overlays.Reveal(AuthOverlay, AuthCard);
    }

    private void CloseAuth()
    {
        Overlays.Hide(AuthOverlay);

        // Nothing typed into the card outlives it. Three of these fields hold credentials,
        // and a card that comes back with the last one still in it is a card that shows a
        // secret to whoever opens it next.
        AuthBearerValue.Text = string.Empty;
        AuthBasicUser.Text = string.Empty;
        AuthBasicPassword.Password = string.Empty;
        AuthApiKeyValue.Text = string.Empty;
        AuthClientSecret.Text = string.Empty;

        _authBlock = null;
        _authText = string.Empty;
    }

    /// <summary>Fills every control from the auth the document declares.</summary>
    private void FillAuthCard(RequestAuthView view)
    {
        _updatingAuthForm = true;

        try
        {
            AuthRequestLabel.Text = _authBlock is { } block
                ? block.Method + "  " + block.Target
                : string.Empty;

            AuthOriginLabel.Text = DescribeOrigin(view);
            DescribeResolution(view);

            var labels = AuthChoices.Select(c => c.Label).ToList();

            // The entry for a header Sling does not write exists only while the request has
            // one. Offering it permanently would be offering to write something this panel
            // has no fields for.
            if (view.Scheme == AuthScheme.Unrecognized)
            {
                labels.Add(UnrecognizedLabel);
            }

            AuthSchemePicker.ItemsSource = labels;
            AuthSchemePicker.SelectedItem = view.Scheme == AuthScheme.Unrecognized
                ? UnrecognizedLabel
                : AuthChoices.First(c => c.Scheme == view.Scheme).Label;

            AuthPlacement.ItemsSource = new[] { BasicHeaderLabel, FormBodyLabel };
            AuthPlacement.SelectedItem = view.Grant?.Placement == ClientAuthPlacement.FormBody
                ? FormBodyLabel
                : BasicHeaderLabel;

            AuthBearerValue.Text = view.Scheme == AuthScheme.Bearer ? view.Written ?? string.Empty : string.Empty;
            AuthApiKeyHeader.Text = view.Scheme == AuthScheme.ApiKeyHeader
                ? view.HeaderName ?? RequestAuth.ApiKeyHeaders[0]
                : RequestAuth.ApiKeyHeaders[0];

            AuthApiKeyValue.Text = view.Scheme == AuthScheme.ApiKeyHeader ? view.Written ?? string.Empty : string.Empty;

            // Basic is deliberately not filled from the document. What is written there is
            // base64 of user:password, or a reference to it, and neither is a user name -
            // decoding one into these boxes would be showing a password the card is about to
            // put back the way it found it.
            AuthBasicUser.Text = string.Empty;
            AuthBasicPassword.Password = string.Empty;

            AuthTokenUrl.Text = view.Grant?.TokenUrl ?? string.Empty;
            AuthClientId.Text = view.Grant?.ClientId ?? string.Empty;
            AuthClientSecret.Text = view.Grant?.ClientSecret ?? string.Empty;
            AuthScope.Text = view.Grant?.Scope ?? string.Empty;
            AuthAudience.Text = view.Grant?.Audience ?? string.Empty;

            AuthUnrecognizedNote.Text = view.Scheme == AuthScheme.Unrecognized
                ? $"This request sends '{view.HeaderName}: {view.Written}'. Sling does not write that "
                    + "scheme, so it is shown rather than edited - change it in the document, or pick "
                    + "another kind above to replace it."
                : string.Empty;
        }
        finally
        {
            _updatingAuthForm = false;
        }

        UpdateAuthFields();
    }

    /// <summary>The sentence naming where the credential is declared.</summary>
    private static string DescribeOrigin(RequestAuthView view) => view.Origin switch
    {
        AuthOrigin.Grant =>
            $"From a '# @auth oauth2' block on line {view.Line.ToString(CultureInfo.InvariantCulture)}. "
                + "Sling fetches the token and caches it for this session.",

        AuthOrigin.Header =>
            $"From a '{view.HeaderName}' header on line {view.Line.ToString(CultureInfo.InvariantCulture)}.",

        _ => "This request sends no credential.",
    };

    /// <summary>
    /// Says whether the variable the credential names actually resolves, and offers to define
    /// it when it does not.
    /// </summary>
    /// <remarks>
    /// <b>The value is never shown, only whether there is one and which file it came from.</b>
    /// That is the same rule the diagnostics keep, and it is the answer people actually need:
    /// a 401 is explained by "that name is not defined in the environment you have selected",
    /// never by the token itself.
    /// </remarks>
    private void DescribeResolution(RequestAuthView view)
    {
        AuthDefineButton.Visibility = Visibility.Collapsed;

        if (view.Variable is not { } variable)
        {
            AuthResolution.Text = view.Origin == AuthOrigin.Header && view.Written is { Length: > 0 }
                ? "Written into the document rather than referenced from an environment."
                : string.Empty;

            return;
        }

        var values = _environments.Select(_selectedEnvironment);
        var where = _selectedEnvironment is { } selected ? $"'{selected}'" : "the shared values";

        if (!values.TryGet(variable, out _))
        {
            AuthResolution.Text = $"'{{{{{variable}}}}}' is not defined in {where}.";
            AuthDefineButton.Visibility = Visibility.Visible;
            return;
        }

        AuthResolution.Text = values.IsSecret(variable)
            ? $"'{{{{{variable}}}}}' resolves from {where} in {Workspace.PrivateEnvironmentFileName}."
            : $"'{{{{{variable}}}}}' resolves from {where} in {Workspace.SharedEnvironmentFileName}.";
    }

    private void OnAuthSchemeChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthFields();

    private void OnAuthFieldChanged(object sender, TextChangedEventArgs e) => UpdateAuthFields();

    private void OnAuthPasswordChanged(object sender, RoutedEventArgs e) => UpdateAuthFields();

    /// <summary>Shows the fields the chosen kind needs, and nothing else.</summary>
    private void UpdateAuthFields()
    {
        if (_updatingAuthForm)
        {
            return;
        }

        var scheme = SelectedAuthScheme();

        AuthNoneFields.Visibility = Show(scheme == AuthScheme.None);
        AuthBearerFields.Visibility = Show(scheme == AuthScheme.Bearer);
        AuthBasicFields.Visibility = Show(scheme == AuthScheme.Basic);
        AuthApiKeyFields.Visibility = Show(scheme == AuthScheme.ApiKeyHeader);
        AuthGrantFields.Visibility = Show(scheme == AuthScheme.ClientCredentials);
        AuthUnrecognizedFields.Visibility = Show(scheme == AuthScheme.Unrecognized);

        // Nothing to apply for a scheme this panel does not write: the entry exists to
        // describe what is there, and Apply would have to guess what to replace it with.
        AuthApplyButton.IsEnabled = scheme != AuthScheme.Unrecognized;

        var literal = LiteralCredential(scheme);

        AuthStoreFields.Visibility = Show(literal is not null);

        if (literal is null)
        {
            return;
        }

        if (AuthStoreName.Text.Length == 0)
        {
            _updatingAuthForm = true;

            try
            {
                AuthStoreName.Text = DefaultStoreName(scheme);
            }
            finally
            {
                _updatingAuthForm = false;
            }
        }

        var environment = _selectedEnvironment ?? EnvironmentSet.SharedName;

        AuthStoreHint.Text =
            $"That is a credential, not a reference, so it goes to {Workspace.PrivateEnvironmentFileName} "
                + $"under '{environment}' and the request gets '{{{{{AuthStoreName.Text.Trim()}}}}}'. "
                + "A literal credential is never written into a .http file.";
    }

    /// <summary>
    /// The credential the user typed, when it is a value rather than a reference.
    /// </summary>
    /// <remarks>
    /// A <c>{{reference}}</c> comes back as null, because there is nothing to store: it
    /// already names a variable, and rewriting it would be inventing a second name for the
    /// value the user already has one for.
    /// </remarks>
    private string? LiteralCredential(AuthScheme scheme)
    {
        var typed = scheme switch
        {
            AuthScheme.Bearer => AuthBearerValue.Text,
            AuthScheme.ApiKeyHeader => AuthApiKeyValue.Text,
            AuthScheme.ClientCredentials => AuthClientSecret.Text,
            AuthScheme.Basic => AuthBasicPassword.Password.Length > 0 ? "basic" : string.Empty,
            _ => string.Empty,
        };

        return typed.Trim() is { Length: > 0 } value && RequestAuth.SoleVariable(value) is null
            ? value
            : null;
    }

    private static string DefaultStoreName(AuthScheme scheme) => scheme switch
    {
        AuthScheme.Bearer => "token",
        AuthScheme.ApiKeyHeader => "api_key",
        AuthScheme.Basic => "basic_auth",
        _ => "client_secret",
    };

    private AuthScheme SelectedAuthScheme()
    {
        if (AuthSchemePicker.SelectedItem is not string label)
        {
            return AuthScheme.None;
        }

        return label == UnrecognizedLabel
            ? AuthScheme.Unrecognized
            : AuthChoices.First(c => c.Label == label).Scheme;
    }

    private void OnAuthDefineVariable(object sender, RoutedEventArgs e)
    {
        var variable = _authVariable;

        CloseAuth();

        // Secret by default: a name missing from an Authorization header or an auth
        // directive is a credential far more often than not.
        ShowEnvironments(variable, secret: true);
    }

    private void OnAuthApply(object sender, RoutedEventArgs e) => RunGuarded(ApplyAuthAsync);

    /// <summary>
    /// Writes the chosen auth into the document, storing any credential first.
    /// </summary>
    /// <remarks>
    /// The secret is written before the document is, and that order is deliberate: the
    /// reverse leaves a request referencing a variable that does not exist if the file write
    /// fails, which is a document that looks correct and cannot send.
    /// </remarks>
    private async Task ApplyAuthAsync()
    {
        if (_authBlock is not { } block)
        {
            return;
        }

        var scheme = SelectedAuthScheme();

        if (scheme == AuthScheme.Unrecognized)
        {
            return;
        }

        AuthError.Visibility = Visibility.Collapsed;

        string? credential;

        try
        {
            credential = await StoreCredentialAsync(scheme).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidDataException)
        {
            AuthError.Text = ex.Message;
            AuthError.Visibility = Visibility.Visible;
            return;
        }

        AuthSetting setting;

        try
        {
            setting = BuildAuthSetting(scheme, credential);
        }
        catch (ArgumentException ex)
        {
            AuthError.Text = ex.Message;
            AuthError.Visibility = Visibility.Visible;
            return;
        }

        var edits = AuthDocumentEditor.Rewrite(_authText, block, setting);

        // Last edit first, and inside one undo group: the offsets were measured against the
        // text as it was, and an earlier edit applied first shifts every later one. Grouping
        // them means Ctrl+Z takes back the change the user made rather than a third of it.
        using (RequestPane.Document.RunUpdate())
        {
            foreach (var edit in edits.OrderByDescending(edit => edit.Offset))
            {
                RequestPane.Document.Replace(edit.Offset, edit.Length, edit.Text);
            }
        }

        CloseAuth();

        RefreshOpenDocumentRequests();
        UpdateSendTarget(reparse: true);

        StatusLeft.Text = scheme == AuthScheme.None
            ? "Removed the auth from this request. Ctrl+S saves it."
            : $"Wrote the auth into {DocumentName}. Ctrl+S saves it.";
    }

    /// <summary>
    /// Puts a typed credential in the secrets file, and answers with the reference to it.
    /// </summary>
    /// <returns>
    /// What the document should carry: a <c>{{reference}}</c>, or whatever was typed when it
    /// already was one.
    /// </returns>
    private async Task<string?> StoreCredentialAsync(AuthScheme scheme)
    {
        var typed = scheme switch
        {
            AuthScheme.Bearer => AuthBearerValue.Text.Trim(),
            AuthScheme.ApiKeyHeader => AuthApiKeyValue.Text.Trim(),
            AuthScheme.ClientCredentials => AuthClientSecret.Text.Trim(),
            AuthScheme.Basic => BasicCredential(),
            _ => string.Empty,
        };

        if (typed.Length == 0 || RequestAuth.SoleVariable(typed) is not null)
        {
            return typed.Length == 0 ? null : typed;
        }

        if (EnsureWorkspace("Choose the folder the secret belongs to") is not { } workspace)
        {
            throw new InvalidDataException(
                "A credential goes in the secrets file beside the request, so this needs a folder to "
                    + "be open.");
        }

        var name = AuthStoreName.Text.Trim();
        var environment = _selectedEnvironment ?? EnvironmentSet.SharedName;

        var written = await EnvironmentEditor
            .SetAsync(workspace, environment, name, typed, secret: true, CancellationToken.None)
            .ConfigureAwait(true);

        // Read back so the panel's own "resolves from" line, and the environment card behind
        // it, are looking at what is on disk rather than at what was there a moment ago.
        ReloadEnvironments();

        StatusLeft.Text = Describe(written);

        return "{{" + name + "}}";
    }

    /// <summary>
    /// The value a Basic header carries: base64 of <c>user:password</c>, per RFC 7617.
    /// </summary>
    /// <remarks>
    /// Encoded here and stored as a secret, exactly as the Postman importer does it. The
    /// alternative - writing <c>user:password</c> into the document and encoding at send
    /// time - would put a password in a file meant to be committed, which is the one thing
    /// neither importer will do.
    /// </remarks>
    private string BasicCredential()
    {
        var user = AuthBasicUser.Text;
        var password = AuthBasicPassword.Password;

        return user.Length == 0 && password.Length == 0
            ? string.Empty
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
    }

    /// <summary>Turns the card's fields into the setting the document editor writes.</summary>
    private AuthSetting BuildAuthSetting(AuthScheme scheme, string? credential)
    {
        if (scheme == AuthScheme.None)
        {
            return new AuthSetting(AuthScheme.None);
        }

        if (scheme != AuthScheme.ClientCredentials)
        {
            if (string.IsNullOrEmpty(credential))
            {
                throw new ArgumentException("There is no credential to write yet.", nameof(scheme));
            }

            return new AuthSetting(scheme, AuthApiKeyHeader.Text.Trim(), credential);
        }

        var tokenUrl = AuthTokenUrl.Text.Trim();
        var clientId = AuthClientId.Text.Trim();

        if (tokenUrl.Length == 0 || clientId.Length == 0 || string.IsNullOrEmpty(credential))
        {
            throw new ArgumentException(
                "A client-credentials grant needs a token URL, a client id and a client secret.",
                nameof(scheme));
        }

        var placement = ReferenceEquals(AuthPlacement.SelectedItem, FormBodyLabel)
            ? ClientAuthPlacement.FormBody
            : ClientAuthPlacement.BasicHeader;

        return new AuthSetting(
            AuthScheme.ClientCredentials,
            Grant: new GrantFields(
                tokenUrl,
                clientId,
                credential,
                AuthScope.Text.Trim(),
                AuthAudience.Text.Trim(),
                placement));
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
