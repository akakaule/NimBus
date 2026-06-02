namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Hard limits that every search/list method on the message-tracking stores
/// (Cosmos DB, SQL Server, in-memory) must honour. Caps the per-call page size
/// so a caller passing a huge <c>maxItemCount</c> (or a buggy client running an
/// unbounded loop) can't trigger an out-of-memory load of the entire
/// table/container into the WebApp process. The store still surfaces a
/// continuation token, so genuinely large result sets stay available — just one
/// capped page at a time.
/// </summary>
public static class PaginationLimits
{
    /// <summary>Default page size when the caller didn't supply one.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>
    /// Maximum allowed page size. Requests above this are clamped silently —
    /// the API contract is "best-effort up to this cap, get more via
    /// continuation token", not "fail loudly when too large".
    /// </summary>
    public const int MaxPageSize = 1000;

    /// <summary>
    /// Clamp a caller-supplied <paramref name="requested"/> page size to
    /// <c>[1, <see cref="MaxPageSize"/>]</c>, falling back to
    /// <see cref="DefaultPageSize"/> when zero/negative.
    /// </summary>
    public static int Resolve(int requested)
    {
        if (requested <= 0) return DefaultPageSize;
        return requested > MaxPageSize ? MaxPageSize : requested;
    }
}
