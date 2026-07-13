using System.Text.Json;

namespace NimBus.CommandLine;

internal sealed class AzureDeploymentCommand : IDisposable
{
    private const string DeploymentParametersSchema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#";
    private readonly List<string> _arguments;
    private readonly Dictionary<string, string> _secureParameters = new(StringComparer.Ordinal);
    private string? _parameterDirectoryPath;
    private string? _parameterFilePath;
    private bool _built;
    private bool _disposed;

    public AzureDeploymentCommand(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _arguments = new List<string>(arguments);
    }

    public void AddSecureParameter(string name, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_built)
        {
            throw new InvalidOperationException("Secure parameters cannot be added after the command is built.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _secureParameters[name] = value;
    }

    public IReadOnlyList<string> BuildArguments()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_built)
        {
            return _arguments;
        }

        _built = true;
        if (_secureParameters.Count == 0)
        {
            return _arguments;
        }

        WriteSecureParameterFile();
        _arguments.Add("--parameters");
        _arguments.Add($"@{_parameterFilePath}");
        return _arguments;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_parameterFilePath is not null && File.Exists(_parameterFilePath))
        {
            File.Delete(_parameterFilePath);
        }

        if (_parameterDirectoryPath is not null && Directory.Exists(_parameterDirectoryPath))
        {
            Directory.Delete(_parameterDirectoryPath);
        }
    }

    private void WriteSecureParameterFile()
    {
        var directory = Directory.CreateTempSubdirectory("nimbus-deployment-");
        _parameterDirectoryPath = directory.FullName;

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _parameterDirectoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _parameterFilePath = Path.Combine(_parameterDirectoryPath, "parameters.json");
        var streamOptions = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };

        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(_parameterFilePath, streamOptions);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("$schema", DeploymentParametersSchema);
        writer.WriteString("contentVersion", "1.0.0.0");
        writer.WriteStartObject("parameters");
        foreach (var parameter in _secureParameters)
        {
            writer.WriteStartObject(parameter.Key);
            writer.WriteString("value", parameter.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
