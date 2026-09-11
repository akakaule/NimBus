namespace NimBus.Adapters.Dataverse;

/// <summary>A permanent input failure with a safe broker dead-letter reason.</summary>
public sealed class DataverseInputException(string reason) : Exception(reason)
{
    /// <summary>Non-sensitive machine-readable failure reason.</summary>
    public string Reason { get; } = reason;
}
