namespace NimBus.MessageStore.States;

/// <summary>
/// Outcome of a single heartbeat probe, and the rolled-up state of an endpoint
/// or platform service.
/// </summary>
public enum HeartbeatStatus
{
    /// <summary>The probe was answered — the endpoint or service is alive.</summary>
    On,

    /// <summary>The probe failed or timed out without an answer.</summary>
    Off,

    /// <summary>The probe was sent and no response has arrived yet.</summary>
    Pending,

    /// <summary>Nothing is known yet — no probe has ever settled.</summary>
    Unknown,

    /// <summary>
    /// The endpoint answered, but with an <c>UnsupportedResponse</c>: it runs an SDK
    /// that predates the heartbeat handler. Reachability is still proven.
    /// </summary>
    Unsupported,
}
