using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NimBus.CommandLine;

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool echoStandardOutput,
        CancellationToken cancellationToken);
}

internal sealed class ProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        RunAsync(fileName, arguments, workingDirectory, echoStandardOutput: true, cancellationToken);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool echoStandardOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        DeploymentSecrets.RemoveFrom(startInfo.Environment);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) && arguments.Count >= 2)
        {
            // cmd.exe requires a raw Arguments string — ArgumentList auto-quoting
            // breaks cmd.exe /c parsing of the command string.
            startInfo.Arguments = string.Join(" ", arguments);
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var output = new StringBuilder();
        var error = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                if (echoStandardOutput)
                {
                    CliOutput.WriteLine(args.Data);
                }

                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                CliOutput.WriteError(args.Data);
                error.AppendLine(args.Data);
            }
        };

        bool started;

        try
        {
            started = process.Start();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new CommandException($"Could not start '{fileName}'. Ensure it is installed and available on PATH.");
        }

        if (!started)
        {
            throw new CommandException($"Failed to start '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deployment command may still be reading an ephemeral ARM
            // parameter file. Stop the whole child tree and wait for shutdown
            // before the caller unwinds and deletes that file.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process won the race and exited between HasExited and Kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
