namespace Erp.Adapter.Functions.Clients;

public interface IServiceModeClient
{
    Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken);

    Task<bool> IsErrorModeEnabledAsync(CancellationToken cancellationToken);
}
