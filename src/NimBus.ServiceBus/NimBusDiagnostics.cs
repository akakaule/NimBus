using System.Diagnostics;

namespace NimBus.ServiceBus;

public static class NimBusDiagnostics
{
    public const string ActivitySourceName = "NimBus";
    public static readonly ActivitySource Source = new(ActivitySourceName);
    public const string DiagnosticIdProperty = "Diagnostic-Id";
}
