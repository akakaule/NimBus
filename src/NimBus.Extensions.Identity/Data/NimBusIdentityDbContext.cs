using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NimBus.Extensions.Identity.Data;

/// <summary>
/// Entity Framework Core database context for NimBus Identity.
/// </summary>
public class NimBusIdentityDbContext : IdentityDbContext<NimBusUser>
{
    private readonly string _schema;

    public NimBusIdentityDbContext(DbContextOptions<NimBusIdentityDbContext> options, NimBusIdentityOptions identityOptions)
        : base(options)
    {
        _schema = identityOptions.Schema;
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(_schema);
    }
}
