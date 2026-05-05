using System.Diagnostics;

namespace NimBus.Core.Diagnostics;

/// <summary>
/// Transport-neutral diagnostic helpers shared by every NimBus assembly that
/// emits OpenTelemetry activities. The <see cref="Source"/> is the single
/// <c>NimBus</c> <see cref="ActivitySource"/> registered by every host via
/// <c>NimBus.ServiceDefaults</c>; the <see cref="DiagnosticIdProperty"/> is the
/// W3C-Trace-Context-compatible header name carried on every transport's wire
/// format.
/// </summary>
public static class NimBusDiagnostics
{
    public const string ActivitySourceName = "NimBus";
    public static readonly ActivitySource Source = new(ActivitySourceName);
    public const string DiagnosticIdProperty = "Diagnostic-Id";
}
