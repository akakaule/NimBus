#pragma warning disable CA1707, CA2007
using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// The Azure deployment keeps the operator MCP endpoint's settings. They are set out of band,
/// after the MCP app registration exists, and app-settings deployment is a full replace.
/// </summary>
[TestClass]
public class McpDeploymentTests
{
    [TestMethod]
    public void Deployment_template_preserves_operator_set_mcp_settings()
    {
        var bicep = File.ReadAllText(LocateRepoFile(Path.Combine("deploy", "bicep", "deploy.webapp.bicep")));

        Assert.IsTrue(
            Regex.IsMatch(bicep, @"startsWith\(setting\.key, 'NimBus__Mcp__'\)"),
            "deploy.webapp.bicep must carry operator-set NimBus__Mcp__* app settings across a redeploy.");
    }

    private static string LocateRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {relativePath} above {AppContext.BaseDirectory}.");
    }
}
