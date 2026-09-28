using Microsoft.EntityFrameworkCore;

namespace BusinessCentral.Api.Data;

public class BcDbContext(DbContextOptions<BcDbContext> options) : DbContext(options)
{
    public DbSet<Salesperson> Salespeople => Set<Salesperson>();
    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CrmOpportunity> CrmOpportunities => Set<CrmOpportunity>();
    public DbSet<SalesQuote> SalesQuotes => Set<SalesQuote>();
    public DbSet<SalesQuoteLine> SalesQuoteLines => Set<SalesQuoteLine>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Salesperson>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(20);
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<ItemCategory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Item>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.CrmAccountId).IsUnique().HasFilter("[CrmAccountId] IS NOT NULL");
            e.HasIndex(x => x.CompanyContactId);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.CrmAccountId).IsUnique().HasFilter("[CrmAccountId] IS NOT NULL");
        });

        modelBuilder.Entity<CrmOpportunity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CrmAccountId);
        });

        modelBuilder.Entity<SalesQuote>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.CrmOpportunityId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SalesQuoteLine>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<SalesOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SalesOrderLine>(e => e.HasKey(x => x.Id));
    }
}
