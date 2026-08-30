using System.Reflection;

namespace Sling.Persistence.Tests;

/// <summary>
/// M0 scaffolding check - see the note in Sling.Http.Tests.WiringTests. Proves the
/// reference chain and the runner work before there is behaviour to test.
/// </summary>
public sealed class WiringTests
{
    [Fact]
    public void Project_assembly_is_referenced_and_loadable()
    {
        var assembly = Assembly.Load("Sling.Persistence");

        Assert.Equal("Sling.Persistence", assembly.GetName().Name);
    }
}
