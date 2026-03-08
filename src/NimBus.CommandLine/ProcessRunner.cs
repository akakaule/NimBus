using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NimBus.CommandLine;

internal sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
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
                CliOutput.WriteLine(args.Data);
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

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
