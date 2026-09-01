namespace NimBus.Core.CircuitBreaker;

/// <summary>Tracks processing outcomes and exposes circuit state for one endpoint.</summary>
public interface IEndpointCircuitBreaker
{
    /// <summary>Raised once for every transition.</summary>
    event Action<CircuitStateChange>? StateChanged;
    /// <summary>Gets the endpoint represented by this breaker.</summary>
    string Endpoint { get; }
    /// <summary>Gets the current state.</summary>
    CircuitState State { get; }
    /// <summary>Records a successfully processed message.</summary>
    void RecordSuccess();
    /// <summary>Records an eligible pipeline failure.</summary>
    void RecordFailure(Exception exception);
    /// <summary>Waits for the next transition observed after this call begins.</summary>
    Task<CircuitStateChange> WaitForStateChangeAsync(CancellationToken cancellationToken);
}

