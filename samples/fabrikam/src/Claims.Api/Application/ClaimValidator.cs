using Fabrikam.Claims.Api.Clients;

namespace Fabrikam.Claims.Api.Application;

public sealed class ClaimValidator(PolicyClient policies)
{
    [BusinessRule("Claim amount cannot exceed policy coverage")]
    public async Task EnsureWithinCoverage(string policyNumber, decimal amount)
    {
        var coverage = await policies.GetCoverageAsync(policyNumber);
        if (coverage is null || !coverage.Active)
        {
            throw new InvalidOperationException($"Policy {policyNumber} is not active.");
        }

        if (amount > coverage.RemainingLimit)
        {
            throw new InvalidOperationException("Claim exceeds the remaining coverage.");
        }
    }
}
