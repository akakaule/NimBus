namespace NimBus.WebApp.ManagementApi;

/// <summary>
/// Server-side defaults for the generated agent-settlement contract.
/// </summary>
public partial class AgentSettleRequest
{
    /// <summary>
    /// Creates a request whose required outcome starts invalid until JSON model
    /// binding explicitly supplies <c>complete</c> or <c>fail</c>. The generated
    /// non-nullable enum otherwise defaults an omitted outcome to <c>complete</c>.
    /// </summary>
    public AgentSettleRequest()
    {
        Outcome = (AgentSettleRequestOutcome)(-1);
    }
}
