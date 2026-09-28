using Microsoft.EntityFrameworkCore;

namespace D365Sales.Api.Data;

public class D365DbContext(DbContextOptions<D365DbContext> options) : DbContext(options)
{
    public DbSet<SystemUser> SystemUsers => Set<SystemUser>();
    public DbSet<ProductGroup> ProductGroups => Set<ProductGroup>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<BcQuoteMirror> BcQuotes => Set<BcQuoteMirror>();
    public DbSet<TimelineEntry> Timeline => Set<TimelineEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemUser>(e => e.HasKey(x => x.SystemUserId));

        modelBuilder.Entity<ProductGroup>(e =>
        {
            e.HasKey(x => x.ProductGroupId);
            e.HasIndex(x => x.CsCode).IsUnique();
        });

        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(x => x.AccountId);
            e.HasIndex(x => x.CsBcCustomerId).IsUnique().HasFilter("[CsBcCustomerId] IS NOT NULL");
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.HasKey(x => x.ContactId);
            e.Ignore(x => x.FullName);
            e.HasIndex(x => x.CsBcContactId).IsUnique().HasFilter("[CsBcContactId] IS NOT NULL");
        });

        modelBuilder.Entity<Lead>(e => e.HasKey(x => x.LeadId));

        modelBuilder.Entity<Opportunity>(e =>
        {
            e.HasKey(x => x.OpportunityId);
            e.HasIndex(x => x.CsNumber).IsUnique();
        });

        modelBuilder.Entity<BcQuoteMirror>(e =>
        {
            e.HasKey(x => x.BcQuoteId);
            e.HasIndex(x => x.OpportunityId);
        });

        modelBuilder.Entity<TimelineEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RegardingId, x.CreatedOn });
        });
    }
}
