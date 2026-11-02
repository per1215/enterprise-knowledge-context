using Fabrikam.Contracts;

namespace Fabrikam.Notifications.Worker;

public sealed class ClaimPaidHandler(ILogger<ClaimPaidHandler> logger) : IIntegrationEventHandler<ClaimPaidIntegrationEvent>
{
    public Task Handle(ClaimPaidIntegrationEvent @event)
    {
        logger.LogInformation("Notify customer: claim {ClaimId} paid ({PaymentId})", @event.ClaimId, @event.PaymentId);
        return Task.CompletedTask;
    }
}
