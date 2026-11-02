using Fabrikam.Policy.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<PolicyDbContext>();

var app = builder.Build();

var policies = app.MapGroup("api/policies");

policies.MapGet("/{policyNumber}", async (string policyNumber, PolicyDbContext db) =>
    await db.Policies.FirstOrDefaultAsync(p => p.Number == policyNumber) is { } policy ? Results.Ok(policy) : Results.NotFound());

policies.MapGet("/{policyNumber}/coverage", GetCoverage);

app.Run();

static async Task<IResult> GetCoverage(string policyNumber, PolicyDbContext db)
{
    var policy = await db.Policies.FirstOrDefaultAsync(p => p.Number == policyNumber);
    return policy is null
        ? Results.NotFound()
        : Results.Ok(new { policy.Number, Active = policy.ExpiresOn > DateOnly.FromDateTime(DateTime.UtcNow), RemainingLimit = policy.CoverageLimit - policy.ClaimedToDate });
}
