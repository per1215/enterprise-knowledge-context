namespace Fabrikam.Claims.Api.Domain;

public sealed class Claim
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public string PolicyNumber { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string Status { get; private set; } = "Submitted";

    public string? ApprovedBy { get; private set; }

    public static Claim Open(string policyNumber, decimal amount, string description) =>
        new() { PolicyNumber = policyNumber, Amount = amount, Description = description };

    // ekc:rule Claims above 10,000 need senior approval
    public void Approve(string approver)
    {
        if (Amount > 10_000 && !approver.StartsWith("senior:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claims above 10,000 need a senior adjuster.");
        }

        Status = "Approved";
        ApprovedBy = approver;
    }

    public void MarkFraudChecked(bool rejected) => Status = rejected ? "Rejected" : "Screened";
}
