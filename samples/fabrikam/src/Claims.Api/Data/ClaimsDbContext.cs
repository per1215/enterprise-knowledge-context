using Fabrikam.Claims.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fabrikam.Claims.Api.Data;

public sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<ClaimDocument> Documents => Set<ClaimDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Claim>().ToTable("claims", "claims");
        modelBuilder.Entity<ClaimDocument>().ToTable("claim_documents", "claims");
    }
}

public sealed class ClaimDocument
{
    public Guid Id { get; set; }

    public Guid ClaimId { get; set; }

    public string FileName { get; set; } = string.Empty;
}
