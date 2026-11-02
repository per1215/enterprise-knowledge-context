using Microsoft.EntityFrameworkCore;

namespace Fabrikam.Payments.Api.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Payment>(b => b.ToTable("payments", "finance"));
}

public sealed class Payment
{
    public Guid Id { get; set; }

    public Guid ClaimId { get; set; }

    public decimal Amount { get; set; }

    public string Iban { get; set; } = string.Empty;
}
