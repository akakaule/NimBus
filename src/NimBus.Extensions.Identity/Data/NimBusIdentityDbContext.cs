using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NimBus.Extensions.Identity.Data;

/// <summary>
/// Entity Framework Core database context for NimBus Identity.
/// </summary>
public class NimBusIdentityDbContext : IdentityDbContext<NimBusUser>
{
    public NimBusIdentityDbContext(DbContextOptions<NimBusIdentityDbContext> options, NimBusIdentityOptions identityOptions)
        : base(options)
    {
        Schema = identityOptions.Schema;
    }

    /// <summary>
    /// The SQL schema this context's tables live in. Surfaced so the model
    /// cache key can distinguish contexts that differ only by schema —
    /// otherwise EF caches a single model (by context type) process-wide and
    /// the first-built schema leaks into every other instance.
    /// </summary>
    internal string Schema { get; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
    }
}
