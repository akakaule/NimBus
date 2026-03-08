using System.Diagnostics.CodeAnalysis;

namespace NimBus.CommandLine;

[SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "The CLI output is intentionally not localized.")]
internal static class CliOutput
{
    public static void WriteLine(string message) => Console.WriteLine(message);

    public static void WriteError(string message) => Console.Error.WriteLine(message);
}
