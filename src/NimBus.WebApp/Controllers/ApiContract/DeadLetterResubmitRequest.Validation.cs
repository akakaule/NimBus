namespace NimBus.WebApp.ManagementApi;

/// <summary>
/// Server-side defaults for the generated Resolver dead-letter replay contract.
/// </summary>
public partial class DeadLetterResubmitRequest
{
    /// <summary>
    /// Creates a request whose required scope starts invalid until JSON model
    /// binding explicitly supplies <c>all</c> or <c>reason</c>.
    /// </summary>
    public DeadLetterResubmitRequest()
    {
        Scope = (DeadLetterResubmitRequestScope)(-1);
    }
}
