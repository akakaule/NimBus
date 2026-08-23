using System.Reflection;

namespace NimBus.CommandLine;

/// <summary>
/// Serves the bicep templates embedded in this package (ADR-015), so infrastructure
/// commands work without a repository clone. The templates are extracted to a
/// version-keyed directory that mirrors the repository layout — <c>deploy/bicep/</c>
/// with its <c>templates/</c> subdirectory — because <c>deploy.core.bicep</c> resolves
/// its modules relative to itself. A flattened extraction would break every
/// <c>module … 'templates/*.bicep'</c> reference.
/// </summary>
internal static class BicepTemplateProvider
{
    private const string ResourcePrefix = "deploy/bicep/";

    private static readonly Lazy<string> ExtractedRoot = new(Extract, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Root of the extracted asset tree, laid out like a repository root: it contains
    /// a <c>deploy/</c> directory. Extraction happens once per process.
    /// </summary>
    public static string AssetsRoot => ExtractedRoot.Value;

    private static string Extract()
    {
        var assembly = typeof(BicepTemplateProvider).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => Normalize(name).StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (resources.Count == 0)
        {
            throw new CommandException(
                "This build of the nb CLI ships no bicep templates. Run the command from a NimBus repository clone or pass --repo-root.");
        }

        // Version-keyed so a CLI upgrade never reuses a previous version's templates,
        // and so repeated runs share one extraction instead of accumulating temp dirs.
        var root = Path.Combine(Path.GetTempPath(), "nb", "assets", ResolveVersion());

        foreach (var resource in resources)
        {
            var relativePath = Normalize(resource)[ResourcePrefix.Length..];
            var destination = Path.Combine(root, "deploy", "bicep", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var source = assembly.GetManifestResourceStream(resource)
                ?? throw new CommandException($"Embedded template '{resource}' could not be read.");
            // Overwrite unconditionally: a previous run interrupted mid-write would
            // otherwise leave a truncated template behind for the life of the version.
            using var target = File.Create(destination);
            source.CopyTo(target);
        }

        return root;
    }

    /// <summary>
    /// Resource names are baked at build time and carry the build platform's directory
    /// separator, so a package built on Linux and one built on Windows disagree.
    /// Normalizing on read makes the lookup independent of where the package was built.
    /// </summary>
    private static string Normalize(string resourceName) => resourceName.Replace('\\', '/');

    internal static string ResolveVersion() =>
        typeof(BicepTemplateProvider).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } informational
            // Strip the '+<sha>' source-revision suffix the SDK appends; it is not part
            // of the release identity and would fragment the extraction directory.
            ? informational.Split('+')[0]
            : typeof(BicepTemplateProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
