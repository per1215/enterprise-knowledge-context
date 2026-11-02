using Microsoft.EntityFrameworkCore;

namespace Fabrikam.Policy.Api.Data;

public sealed class PolicyDbContext(DbContextOptions<PolicyDbContext> options) : DbContext(options)
{
    public DbSet<InsurancePolicy> Policies => Set<InsurancePolicy>();
}

public sealed class InsurancePolicyConfiguration : IEntityTypeConfiguration<InsurancePolicy>
{
    public void Configure(EntityTypeBuilder<InsurancePolicy> builder) => builder.ToTable("policies", "policy");
}

public sealed class InsurancePolicy
{
    public string Number { get; set; } = string.Empty;

    public DateOnly ExpiresOn { get; set; }

    public decimal CoverageLimit { get; set; }

    public decimal ClaimedToDate { get; set; }
}
