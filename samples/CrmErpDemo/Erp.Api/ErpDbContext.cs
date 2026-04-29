using Erp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api;

public class ErpDbContext(DbContextOptions<ErpDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ErpContact> Contacts => Set<ErpContact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerNumber).IsRequired().HasMaxLength(40);
            e.HasIndex(x => x.CustomerNumber).IsUnique();
            e.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
            e.Property(x => x.CountryCode).IsRequired().HasMaxLength(2);
            e.Property(x => x.TaxId).HasMaxLength(40);
            e.HasIndex(x => x.CrmAccountId);
        });

        modelBuilder.Entity<ErpContact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => x.CustomerId);
        });
    }
}
