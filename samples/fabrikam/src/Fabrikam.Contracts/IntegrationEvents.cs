namespace Fabrikam.Contracts;

public record IntegrationEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public record ClaimSubmittedIntegrationEvent(Guid ClaimId, string PolicyNumber, decimal Amount) : IntegrationEvent;

public record ClaimFraudCheckedIntegrationEvent(Guid ClaimId, double Score, bool Rejected) : IntegrationEvent;

public record ClaimApprovedIntegrationEvent(Guid ClaimId, decimal Amount, string PayeeIban) : IntegrationEvent;

public record ClaimPaidIntegrationEvent(Guid ClaimId, Guid PaymentId) : IntegrationEvent;

public interface IEventBus
{
    Task PublishAsync(IntegrationEvent @event);
}

public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task Handle(TEvent @event);
}

public interface IConsumer<in TMessage>
{
    Task Consume(TMessage message);
}
