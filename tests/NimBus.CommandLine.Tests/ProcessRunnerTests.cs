#pragma warning disable CA1707, CA1861, CA2007

using System.Diagnostics;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancellationTerminatesChildProcess()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"nimbus-child-{Guid.NewGuid():N}.pid");
        using var cancellation = new CancellationTokenSource();
        var (fileName, arguments) = BuildLongRunningCommand(pidFile);

        try
        {
            var run = new ProcessRunner().RunAsync(
                fileName,
                arguments,
                workingDirectory: null,
                echoStandardOutput: false,
                cancellation.Token);

            await WaitForFileAsync(pidFile);
            var processId = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            Assert.True(WaitForProcessExit(processId), $"Child process {processId} remained alive after cancellation.");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments) BuildLongRunningCommand(string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var escapedPath = pidFile.Replace("'", "''", StringComparison.Ordinal);
            return (
                "powershell.exe",
                new[]
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"$PID | Set-Content -LiteralPath '{escapedPath}'; Start-Sleep -Seconds 30",
                });
        }

        var shellPath = pidFile.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        return ("/bin/sh", new[] { "-c", $"echo $$ > '{shellPath}'; sleep 30" });
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The child process did not publish its process id.");
    }

    private static bool WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
