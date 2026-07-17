#pragma warning disable CA1707, CA1861, CA2007

using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancellationTerminatesProcessTree()
    {
        var parentPidFile = Path.Combine(Path.GetTempPath(), $"nimbus-parent-{Guid.NewGuid():N}.pid");
        var descendantPidFile = Path.Combine(Path.GetTempPath(), $"nimbus-descendant-{Guid.NewGuid():N}.pid");
        using var cancellation = new CancellationTokenSource();
        var (fileName, arguments) = BuildLongRunningCommand(parentPidFile, descendantPidFile);
        int? parentProcessId = null;
        int? descendantProcessId = null;

        try
        {
            var run = new ProcessRunner().RunAsync(
                fileName,
                arguments,
                workingDirectory: null,
                echoStandardOutput: false,
                cancellation.Token);

            await WaitForFileAsync(parentPidFile);
            await WaitForFileAsync(descendantPidFile);
            parentProcessId = int.Parse(
                await File.ReadAllTextAsync(parentPidFile),
                System.Globalization.CultureInfo.InvariantCulture);
            descendantProcessId = int.Parse(
                await File.ReadAllTextAsync(descendantPidFile),
                System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            Assert.True(
                WaitForProcessExit(parentProcessId.Value),
                $"Parent process {parentProcessId} remained alive after cancellation.");
            Assert.True(
                WaitForProcessExit(descendantProcessId.Value),
                $"Descendant process {descendantProcessId} remained alive after cancellation.");
        }
        finally
        {
            TryKillProcess(parentProcessId);
            TryKillProcess(descendantProcessId);
            File.Delete(parentPidFile);
            File.Delete(descendantPidFile);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationPreservesOperationCanceledWhenTerminationFails()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"nimbus-race-{Guid.NewGuid():N}.pid");
        using var cancellation = new CancellationTokenSource();
        var (fileName, arguments) = BuildSingleLongRunningCommand(pidFile);
        int? processId = null;
        var termination = new FailingProcessTermination();
        var runner = new ProcessRunner(termination, TimeSpan.FromMilliseconds(100));

        try
        {
            var run = runner.RunAsync(
                fileName,
                arguments,
                workingDirectory: null,
                echoStandardOutput: false,
                cancellation.Token);

            await WaitForFileAsync(pidFile);
            processId = int.Parse(
                await File.ReadAllTextAsync(pidFile),
                System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();

            var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(run, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(1, termination.KillCalls);
            Assert.Equal(1, termination.WaitCalls);
        }
        finally
        {
            TryKillProcess(processId);
            File.Delete(pidFile);
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments) BuildLongRunningCommand(
        string parentPidFile,
        string descendantPidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var escapedParentPath = parentPidFile.Replace("'", "''", StringComparison.Ordinal);
            var escapedDescendantPath = descendantPidFile.Replace("'", "''", StringComparison.Ordinal);
            return (
                "powershell.exe",
                new[]
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$child = Start-Process -FilePath 'powershell.exe' " +
                    "-ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' " +
                    "-PassThru -WindowStyle Hidden; " +
                    $"$PID | Set-Content -LiteralPath '{escapedParentPath}'; " +
                    $"$child.Id | Set-Content -LiteralPath '{escapedDescendantPath}'; " +
                    "Wait-Process -Id $child.Id",
                });
        }

        var parentShellPath = parentPidFile.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        var descendantShellPath = descendantPidFile.Replace("'", "'\"'\"'", StringComparison.Ordinal);
        return (
            "/bin/sh",
            new[]
            {
                "-c",
                $"echo $$ > '{parentShellPath}'; sleep 30 & child=$!; echo $child > '{descendantShellPath}'; wait $child",
            });
    }

    private static (string FileName, IReadOnlyList<string> Arguments) BuildSingleLongRunningCommand(
        string pidFile)
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

    private static void TryKillProcess(int? processId)
    {
        if (!processId.HasValue)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed class FailingProcessTermination : IProcessTermination
    {
        public int KillCalls { get; private set; }

        public int WaitCalls { get; private set; }

        public void KillEntireProcessTree(Process process)
        {
            KillCalls++;
            throw new Win32Exception(5, "Simulated access denied.");
        }

        public Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
