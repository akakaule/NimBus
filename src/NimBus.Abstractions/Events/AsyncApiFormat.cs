namespace NimBus.Core.Events
{
    /// <summary>Output format for the AsyncAPI 3.0 document exporter.</summary>
    /// <remarks>
    /// Declared in <c>NimBus.Abstractions</c> so the CommandLine exporter, the SDK fluent
    /// export bridge, and the WebApp download endpoint can all name the format without any
    /// project depending on <c>NimBus.CommandLine</c>.
    /// </remarks>
    public enum AsyncApiFormat
    {
        /// <summary>AsyncAPI 3.0 as YAML (default).</summary>
        Yaml,

        /// <summary>AsyncAPI 3.0 as JSON.</summary>
        Json,
    }
}
