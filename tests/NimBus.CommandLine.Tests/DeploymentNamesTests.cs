using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class DeploymentNamesTests
{
    [Fact]
    public void RecordEquality_WorksForIdenticalValues()
    {
        var a = new DeploymentNames("sol", "env", "sb-sol-env", "ai-sol-env-global-tracelog", "cosmos-sol-env", "func-sol-env-resolver", "webapp-sol-env-management");
        var b = new DeploymentNames("sol", "env", "sb-sol-env", "ai-sol-env-global-tracelog", "cosmos-sol-env", "func-sol-env-resolver", "webapp-sol-env-management");

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DiffersForDifferentValues()
    {
        var a = new DeploymentNames("sol", "dev", "sb-sol-dev", "ai-sol-dev-global-tracelog", "cosmos-sol-dev", "func-sol-dev-resolver", "webapp-sol-dev-management");
        var b = new DeploymentNames("sol", "prod", "sb-sol-prod", "ai-sol-prod-global-tracelog", "cosmos-sol-prod", "func-sol-prod-resolver", "webapp-sol-prod-management");

        Assert.NotEqual(a, b);
    }
}
