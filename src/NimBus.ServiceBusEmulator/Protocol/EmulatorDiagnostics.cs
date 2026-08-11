using System.Diagnostics.CodeAnalysis;

namespace NimBus.ServiceBusEmulator.Protocol;

internal static class EmulatorDiagnostics
{
    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters")]
    public static void Write(string operation, string? identifier = null)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_DIAGNOSTICS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(identifier is null ? operation : $"{operation}: {identifier}");
    }
}
