namespace NimBus.Core.Events
{
    /// <summary>
    /// Host-implemented, public parameterless factory that builds an <see cref="IAsyncApiDocumentProvider"/>.
    /// <c>nb asyncapi export --assembly &lt;host.dll&gt;</c> uses this to reach a provider registered through
    /// the SDK's <c>AddNimBusAsyncApiDocument</c> — that provider is a private, DI-backed type with
    /// constructor dependencies the standalone CLI cannot instantiate directly. A host exposes this public
    /// parameterless factory (typically building its DI container and resolving the provider from it) so the
    /// same fluent <c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c> enrichment surfaces from the CLI export.
    /// Declared in <c>NimBus.Abstractions</c> so hosts and the CLI bind to it without a cross-project dependency.
    /// </summary>
    public interface IAsyncApiDocumentProviderFactory
    {
        /// <summary>
        /// Builds the document provider, e.g. by composing the host's DI container and resolving
        /// <see cref="IAsyncApiDocumentProvider"/> from it.
        /// </summary>
        IAsyncApiDocumentProvider Create();
    }
}
