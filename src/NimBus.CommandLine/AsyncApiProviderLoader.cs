using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NimBus.Core.Events;

namespace NimBus.CommandLine;

/// <summary>
/// Loads an <see cref="IAsyncApiDocumentProvider"/> from a host assembly so
/// <c>nb asyncapi export --assembly &lt;path&gt;</c> can emit a document enriched with fluent
/// <c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c> metadata. Fluent enrichment is a runtime construct the
/// standalone CLI cannot otherwise observe; a host exposes it by registering an
/// <see cref="IAsyncApiDocumentProvider"/> (e.g. via <c>AddNimBusAsyncApiDocument</c>) on a public,
/// parameterless type the CLI can instantiate. Mirrors the reflection convention the WebApp already
/// uses to load an <c>IPlatform</c> by type/assembly.
/// </summary>
public static class AsyncApiProviderLoader
{
    /// <summary>
    /// Loads the assembly at <paramref name="assemblyPath"/> and resolves its document provider,
    /// optionally selecting the type named <paramref name="providerTypeName"/>.
    /// </summary>
    public static IAsyncApiDocumentProvider Load(string assemblyPath, string? providerTypeName = null)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new ArgumentException("Assembly path is required.", nameof(assemblyPath));

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Assembly not found: '{fullPath}'.", fullPath);

        var assembly = Assembly.LoadFrom(fullPath);
        return Resolve(assembly, providerTypeName);
    }

    /// <summary>
    /// Resolves a public, concrete, parameterless <see cref="IAsyncApiDocumentProvider"/> from
    /// <paramref name="assembly"/>. When <paramref name="providerTypeName"/> is null the assembly must
    /// expose exactly one candidate; otherwise the type is matched by full or simple name.
    /// </summary>
    public static IAsyncApiDocumentProvider Resolve(Assembly assembly, string? providerTypeName = null)
    {
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));

        var candidates = assembly.GetExportedTypes()
            .Where(t => typeof(IAsyncApiDocumentProvider).IsAssignableFrom(t)
                        && t is { IsClass: true, IsAbstract: false }
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .ToList();

        Type providerType;
        if (!string.IsNullOrWhiteSpace(providerTypeName))
        {
            providerType = candidates.FirstOrDefault(t =>
                string.Equals(t.FullName, providerTypeName, StringComparison.Ordinal)
                || string.Equals(t.Name, providerTypeName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No public parameterless IAsyncApiDocumentProvider named '{providerTypeName}' was found in '{assembly.GetName().Name}'.");
        }
        else if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No public parameterless IAsyncApiDocumentProvider was found in '{assembly.GetName().Name}'. Expose one, or pass --provider <type>.");
        }
        else if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple IAsyncApiDocumentProvider types were found in '{assembly.GetName().Name}': "
                + string.Join(", ", candidates.Select(t => t.FullName))
                + ". Disambiguate with --provider <type>.");
        }
        else
        {
            providerType = candidates[0];
        }

        return (IAsyncApiDocumentProvider)Activator.CreateInstance(providerType)!;
    }
}
