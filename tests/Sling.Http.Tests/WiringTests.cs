using System.Reflection;

namespace Sling.Http.Tests;

/// <summary>
/// M0 scaffolding check: proves the project reference chain and the test runner are
/// wired up before there is any behaviour to test. Without it the runner reports
/// "No test is available", which is indistinguishable from a broken discoverer.
/// </summary>
/// <remarks>
/// Deliberately asserts only that the assembly loads - nothing about its contents - so
/// it stays true as M1 fills the project in rather than becoming a test that has to be
/// deleted the moment real code arrives.
/// </remarks>
public sealed class WiringTests
{
    [Fact]
    public void Project_assembly_is_referenced_and_loadable()
    {
        var assembly = Assembly.Load("Sling.Http");

        Assert.Equal("Sling.Http", assembly.GetName().Name);
    }
}
