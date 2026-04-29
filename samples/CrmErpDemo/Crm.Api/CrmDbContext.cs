using Crm.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api;

public class CrmDbContext(DbContextOptions<CrmDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Contact> Contacts => Set<Contact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
            e.Property(x => x.CountryCode).IsRequired().HasMaxLength(2);
            e.Property(x => x.TaxId).HasMaxLength(40);
            e.Property(x => x.ErpCustomerNumber).HasMaxLength(40);
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => x.AccountId);
        });
    }
}
