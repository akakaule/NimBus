namespace NimBus.Core.CircuitBreaker;

/// <summary>Identifies the current endpoint circuit state.</summary>
public enum CircuitState
{
    /// <summary>Messages are processed at configured concurrency.</summary>
    Closed = 0,
    /// <summary>Receivers are paused without settling queued messages.</summary>
    Open = 1,
    /// <summary>Messages are processed at one concurrent session as probes.</summary>
    HalfOpen = 2,
}

/// <summary>Describes one endpoint circuit transition.</summary>
public sealed record CircuitStateChange(
    string Endpoint,
    CircuitState From,
    CircuitState To,
    string Reason,
    DateTimeOffset Timestamp);
