namespace NimBus.Core.Events
{
    /// <summary>
    /// Produces an AsyncAPI 3.0 document for the host that resolves it. Registered by
    /// <c>AddNimBusAsyncApiDocument</c> so the very application container that ran the fluent
    /// <c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c> registrations can export its own enriched document.
    /// Declared in <c>NimBus.Abstractions</c> so consumers (the WebApp, tests) bind to it without
    /// referencing <c>NimBus.CommandLine</c>.
    /// </summary>
    public interface IAsyncApiDocumentProvider
    {
        /// <summary>Serializes the AsyncAPI document in the requested <paramref name="format"/>.</summary>
        string GetDocument(AsyncApiFormat format);
    }
}
