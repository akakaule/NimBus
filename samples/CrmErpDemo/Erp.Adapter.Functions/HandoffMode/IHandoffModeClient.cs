namespace Erp.Adapter.Functions.HandoffMode;

public interface IHandoffModeClient
{
    Task<HandoffModeSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed record HandoffModeSnapshot(bool Enabled, int DurationSeconds, double FailureRate);
