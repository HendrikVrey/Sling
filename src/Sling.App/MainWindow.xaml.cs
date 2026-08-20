using Wpf.Ui.Controls;

namespace Sling.App;

// FluentWindow, not Window: the XAML root is ui:FluentWindow, and a partial class whose
// halves name different base types is CS0263. FluentWindow is what gives the Mica
// backdrop and the rounded corners declared in MainWindow.xaml.
public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// Seeded into the request pane on first run so the window is never empty and the
    /// <c>.http</c> dialect is visible immediately. Replaced by a real document store
    /// in M3; it is a literal here rather than a file on disk because M0 deliberately
    /// has no persistence layer wired up.
    /// </summary>
    private const string SampleRequest = """
        @base = https://api.github.com

        ### a request is a document, not a form
        GET {{base}}/repos/HendrikVrey/Sling
        Accept: application/vnd.github+json

        ### named requests chain, so a token can flow into the next call (M1)
        # @name login
        POST {{base}}/auth
        Content-Type: application/json

        { "user": "ada", "pass": "{{secret}}" }

        ###
        GET {{base}}/me
        Authorization: Bearer {{login.response.body.$.access_token}}
        """;

    public MainWindow()
    {
        InitializeComponent();

        RequestPane.Text = SampleRequest;
        ResponsePane.Text = "Nothing sent yet.\n\nSending arrives in M1 — see Sling.md §7.";

        StatusLeft.Text = "M0 shell — parser, sending and transforms are not wired up yet";
        StatusRight.Text = "no request sent";
    }
}
