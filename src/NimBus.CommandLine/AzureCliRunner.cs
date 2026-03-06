using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class AzureCliRunner
{
    private readonly ProcessRunner _processRunner = new();

    public async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        var result = await RunAzAsync(new[] { "account", "show" }, cancellationToken, throwOnFailure: false).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException("Azure CLI is not logged in. Run 'az login' first.");
        }
    }

    public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken) =>
        EnsureSuccessAsync(
            new[] { "extension", "add", "--name", extensionName, "--upgrade" },
            cancellationToken,
            $"Failed to install or update Azure CLI extension '{extensionName}'.");

    public Task RegisterProviderAsync(string providerNamespace, CancellationToken cancellationToken) =>
        EnsureSuccessAsync(
            new[] { "provider", "register", "--namespace", providerNamespace },
            cancellationToken,
            $"Failed to register Azure provider '{providerNamespace}'.");

    public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage) =>
        EnsureSuccessAsync(arguments, workingDirectory: null, cancellationToken, failureMessage);

    public async Task EnsureSuccessAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
    {
        var result = await RunAzAsync(arguments, cancellationToken, workingDirectory: workingDirectory, throwOnFailure: false).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException($"{failureMessage}{Environment.NewLine}{result.StandardError}");
        }
    }

    public Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage) =>
        CaptureValueAsync(arguments, workingDirectory: null, cancellationToken, failureMessage);

    public async Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
    {
        var result = await RunAzAsync(arguments, cancellationToken, workingDirectory: workingDirectory, throwOnFailure: false).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException($"{failureMessage}{Environment.NewLine}{result.StandardError}");
        }

        return result.StandardOutput.Trim();
    }

    public async Task<JsonDocument> CaptureJsonAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
    {
        var payload = await CaptureValueAsync(arguments, cancellationToken, failureMessage).ConfigureAwait(false);
        return JsonDocument.Parse(payload);
    }

    public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAzAsync(arguments, cancellationToken, throwOnFailure: false);

    private async Task<ProcessResult> RunAzAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, bool throwOnFailure = true)
    {
        var allArguments = new List<string>(arguments.Count + 1)
        {
            "--only-show-errors",
        };
        allArguments.AddRange(arguments);

        var result = await _processRunner.RunAsync("az", allArguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (throwOnFailure && !result.Succeeded)
        {
            throw new CommandException(result.StandardError);
        }

        return result;
    }
}
