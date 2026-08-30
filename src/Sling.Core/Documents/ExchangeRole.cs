namespace Sling.Core.Documents;

/// <summary>
/// Why an exchange happened: because it was asked for, or because Sling decided it had to.
/// </summary>
/// <remarks>
/// <para>
/// Every network call Sling makes on the user's behalf already appears in the response
/// picker, and until this existed each one rendered as an ordinary method and URL row. The
/// only tell that a token exchange was not something you asked for was that you did not
/// recognise it.
/// </para>
/// <para>
/// Auto-sending a dependency is a deliberate divergence from the reference dialect and the
/// right one, but it stays defensible only while every call made on your behalf is visible
/// as one. The retry after a 401 makes that load-bearing rather than merely tidy: without a
/// label, a request that failed and then quietly succeeded is a mystery success.
/// </para>
/// <para>
/// In <c>Sling.Core</c> rather than beside <c>Exchange</c>, which lives in the project that
/// touches the network, so that the words each role is described with can live beside the
/// rest of the rendering rules instead of in a code-behind where nothing can check them.
/// </para>
/// </remarks>
public enum ExchangeRole
{
    /// <summary>The request the user pressed send on.</summary>
    Requested,

    /// <summary>Sent because a <c>{{name.response…}}</c> reference needed its response.</summary>
    Dependency,

    /// <summary>An OAuth2 token exchange Sling performed to satisfy a grant.</summary>
    TokenRequest,

    /// <summary>The same request again, after a 401 sent Sling back for a fresh token.</summary>
    Retry,
}
