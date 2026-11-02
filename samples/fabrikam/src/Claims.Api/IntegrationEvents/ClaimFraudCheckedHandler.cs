using Fabrikam.Claims.Api.Data;
using Fabrikam.Contracts;

namespace Fabrikam.Claims.Api.IntegrationEvents;

public sealed class ClaimFraudCheckedHandler(ClaimsDbContext db) : IIntegrationEventHandler<ClaimFraudCheckedIntegrationEvent>
{
    public async Task Handle(ClaimFraudCheckedIntegrationEvent @event)
    {
        var claim = await db.Claims.FindAsync(@event.ClaimId);
        claim?.MarkFraudChecked(@event.Rejected);
        await db.SaveChangesAsync();
    }
}
