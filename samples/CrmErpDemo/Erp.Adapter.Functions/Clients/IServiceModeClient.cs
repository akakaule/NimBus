namespace Erp.Adapter.Functions.Clients;

public interface IServiceModeClient
{
    Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The current error-mode switch and the failure reason selected in erp-web.
    /// Fail-open: when erp-api cannot be read the mode reports disabled.
    /// </summary>
    Task<ErrorModeSnapshot> GetErrorModeAsync(CancellationToken cancellationToken);
}

/// <param name="Enabled">Whether handlers should throw.</param>
/// <param name="Reason">An <c>ErpFailureReasons</c> id; null means the default generic failure.</param>
public sealed record ErrorModeSnapshot(bool Enabled, string? Reason)
{
    public static readonly ErrorModeSnapshot Disabled = new(false, null);
}
