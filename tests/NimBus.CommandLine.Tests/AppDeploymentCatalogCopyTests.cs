using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Covers <see cref="AppDeploymentService.CopyExternalCatalogAssemblies"/>: `nb deploy apps
/// --assembly` (and `nb setup --assembly`) must bundle the external catalog DLL — plus its
/// private dependencies sitting next to it — into the WebApp publish output root, without
/// clobbering assemblies the publish output already ships.
/// </summary>
public sealed class AppDeploymentCatalogCopyTests : IDisposable
{
    private readonly string _root;
    private readonly string _catalogDirectory;
    private readonly string _publishDirectory;

    public AppDeploymentCatalogCopyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nb-tests", Guid.NewGuid().ToString("N"));
        _catalogDirectory = Path.Combine(_root, "catalog");
        _publishDirectory = Path.Combine(_root, "publish");
        Directory.CreateDirectory(_catalogDirectory);
        Directory.CreateDirectory(_publishDirectory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Copies_the_catalog_assembly_and_missing_sibling_dlls()
    {
        var catalogPath = WriteFile(_catalogDirectory, "MyCompany.Catalog.dll", "catalog");
        WriteFile(_catalogDirectory, "MyCompany.Contracts.dll", "contracts");
        WriteFile(_catalogDirectory, "readme.txt", "not a dll");

        var copied = AppDeploymentService.CopyExternalCatalogAssemblies(catalogPath, _publishDirectory);

        Assert.Equal(new[] { "MyCompany.Catalog.dll", "MyCompany.Contracts.dll" }, copied);
        Assert.Equal("catalog", File.ReadAllText(Path.Combine(_publishDirectory, "MyCompany.Catalog.dll")));
        Assert.Equal("contracts", File.ReadAllText(Path.Combine(_publishDirectory, "MyCompany.Contracts.dll")));
        Assert.False(File.Exists(Path.Combine(_publishDirectory, "readme.txt")));
    }

    [Fact]
    public void Sibling_dlls_already_in_the_publish_output_are_not_overwritten()
    {
        // NimBus.Core.dll next to the catalog is the version the catalog was built
        // against; the freshly published WebApp already ships its own. The published
        // copy must win, or the package ends up with mismatched platform assemblies.
        var catalogPath = WriteFile(_catalogDirectory, "MyCompany.Catalog.dll", "catalog");
        WriteFile(_catalogDirectory, "NimBus.Core.dll", "catalog-build copy");
        WriteFile(_publishDirectory, "NimBus.Core.dll", "published copy");

        var copied = AppDeploymentService.CopyExternalCatalogAssemblies(catalogPath, _publishDirectory);

        Assert.Equal(new[] { "MyCompany.Catalog.dll" }, copied);
        Assert.Equal("published copy", File.ReadAllText(Path.Combine(_publishDirectory, "NimBus.Core.dll")));
    }

    [Fact]
    public void The_catalog_assembly_itself_overwrites_a_stale_copy_in_the_publish_output()
    {
        var catalogPath = WriteFile(_catalogDirectory, "MyCompany.Catalog.dll", "new catalog");
        WriteFile(_publishDirectory, "MyCompany.Catalog.dll", "stale catalog");

        var copied = AppDeploymentService.CopyExternalCatalogAssemblies(catalogPath, _publishDirectory);

        Assert.Equal(new[] { "MyCompany.Catalog.dll" }, copied);
        Assert.Equal("new catalog", File.ReadAllText(Path.Combine(_publishDirectory, "MyCompany.Catalog.dll")));
    }

    [Fact]
    public void A_missing_catalog_assembly_fails_with_the_resolved_path()
    {
        var missing = Path.Combine(_catalogDirectory, "Nope.dll");

        var exception = Assert.Throws<CommandException>(
            () => AppDeploymentService.CopyExternalCatalogAssemblies(missing, _publishDirectory));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    private static string WriteFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
