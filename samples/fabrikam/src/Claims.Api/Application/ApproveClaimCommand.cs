using Fabrikam.Claims.Api.Data;
using Fabrikam.Contracts;

namespace Fabrikam.Claims.Api.Application;

public sealed record ApproveClaimCommand(Guid ClaimId, string ApprovedBy, string PayeeIban) : IRequest<bool>;

public sealed class ApproveClaimCommandHandler(ClaimsDbContext db, IEventBus bus) : IRequestHandler<ApproveClaimCommand, bool>
{
    public async Task<bool> Handle(ApproveClaimCommand command, CancellationToken cancellationToken)
    {
        var claim = await db.Claims.FindAsync(command.ClaimId);
        if (claim is null)
        {
            return false;
        }

        claim.Approve(command.ApprovedBy);
        await db.SaveChangesAsync(cancellationToken);

        await bus.PublishAsync(new ClaimApprovedIntegrationEvent(claim.Id, claim.Amount, command.PayeeIban));
        return true;
    }
}
