using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NimBus.Extensions.Identity.Data;

/// <summary>
/// Includes the configured schema in the EF Core model cache key.
/// <para>
/// EF caches one compiled model per context type, and that cache is shared
/// across every <see cref="NimBusIdentityDbContext"/> built from the same
/// provider options (same connection string). Because the schema is applied
/// in <c>OnModelCreating</c> via <c>HasDefaultSchema</c>, the default
/// type-only key would let the first instance's schema win for all others —
/// fine in a single-schema deployment, but it cross-contaminates any host
/// that runs multiple schemas in one process (e.g. the test harness's
/// per-test schemas). Keying on the schema as well keeps each schema's model
/// distinct.
/// </para>
/// </summary>
internal sealed class NimBusIdentityModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is NimBusIdentityDbContext identityContext
            ? (context.GetType(), identityContext.Schema, designTime)
            : context.GetType();
}
