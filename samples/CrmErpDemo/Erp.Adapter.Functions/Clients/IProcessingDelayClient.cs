namespace Erp.Adapter.Functions.Clients;

public interface IProcessingDelayClient
{
    /// <summary>
    /// The effective per-message processing delay in milliseconds, or 0 when the
    /// setting is disabled or can't be read (so processing proceeds without delay).
    /// </summary>
    Task<int> GetProcessingDelayMsAsync(CancellationToken cancellationToken);
}
