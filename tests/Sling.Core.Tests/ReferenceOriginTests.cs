using System.Diagnostics.CodeAnalysis;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Core.Tests;

/// <summary>
/// Where a reference resolves from, in the order sending would look.
/// </summary>
/// <remarks>
/// The auth panel used to consult only the environment, so a chained token was reported as
/// "not defined" with an offer to define it. These pin the order the resolver uses.
/// </remarks>
public sealed class ReferenceOriginTests
{
    private const string Document = """
        @user = ada
        @user = grace

        ### Log in
        # @name login
        POST https://api.example.com/auth

        ### Me
        GET https://api.example.com/me
        Authorization: Bearer {{login.response.body.$.access_token}}
        """;

    [Fact]
    public void A_chained_reference_comes_from_the_named_response()
    {
        var origin = Locate("login.response.body.$.access_token");

        Assert.Equal(ReferenceSource.Response, origin.Source);
        Assert.Equal("login", origin.RequestName);
        Assert.Equal(6, origin.Line);
    }

    [Fact]
    public void A_chained_reference_to_an_undeclared_name_says_so()
    {
        var origin = Locate("signin.response.body.$.token");

        Assert.Equal(ReferenceSource.MissingRequest, origin.Source);
        Assert.Equal("signin", origin.RequestName);
    }

    [Fact]
    public void A_file_variable_is_found_at_its_last_definition()
    {
        var origin = Locate("user");

        Assert.Equal(ReferenceSource.File, origin.Source);
        Assert.Equal(2, origin.Line);
    }

    [Fact]
    public void The_environment_outranks_the_file_as_it_does_when_sending()
    {
        var origin = Locate("user", new Values("user"));

        Assert.Equal(ReferenceSource.Environment, origin.Source);
    }

    [Fact]
    public void A_name_defined_nowhere_is_undefined()
    {
        Assert.Equal(ReferenceSource.Undefined, Locate("token").Source);
    }

    [Fact]
    public void A_chained_reference_is_not_shadowed_by_an_environment_value_of_the_same_text()
    {
        // The resolver tries the chain grammar first; the panel must too.
        var origin = Locate("login.response.body.$.access_token", new Values("login.response.body.$.access_token"));

        Assert.Equal(ReferenceSource.Response, origin.Source);
    }

    private static ReferenceOrigin Locate(string reference, IVariableSource? environment = null) =>
        ReferenceOrigin.Locate(reference, RequestDocumentParser.Parse(Document), environment ?? NoVariables.Instance);

    private sealed class Values(params string[] names) : IVariableSource
    {
        public bool TryGet(string name, [NotNullWhen(true)] out string? value)
        {
            value = names.Contains(name) ? "x" : null;
            return value is not null;
        }
    }
}
