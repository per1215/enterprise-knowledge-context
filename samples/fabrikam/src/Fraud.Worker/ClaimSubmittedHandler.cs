using Fabrikam.Contracts;

namespace Fabrikam.Fraud.Worker;

public sealed class ClaimSubmittedHandler(FraudScorer scorer, IEventBus bus) : IIntegrationEventHandler<ClaimSubmittedIntegrationEvent>
{
    public async Task Handle(ClaimSubmittedIntegrationEvent @event)
    {
        var score = scorer.Score(@event.PolicyNumber, @event.Amount);
        await bus.PublishAsync(new ClaimFraudCheckedIntegrationEvent(@event.ClaimId, score, score > 0.8));
    }
}

public sealed class FraudScorer
{
    public double Score(string policyNumber, decimal amount) =>
        amount > 50_000 ? 0.9 : policyNumber.StartsWith("TMP", StringComparison.Ordinal) ? 0.6 : 0.1;
}
