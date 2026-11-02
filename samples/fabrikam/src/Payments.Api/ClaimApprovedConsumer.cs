using Fabrikam.Contracts;
using Fabrikam.Payments.Api.Data;

namespace Fabrikam.Payments.Api;

/// <summary>MassTransit-style consumer: subscribes by implementing IConsumer&lt;T&gt;.</summary>
public sealed class ClaimApprovedConsumer(PaymentsDbContext db, IEventBus bus) : IConsumer<ClaimApprovedIntegrationEvent>
{
    public async Task Consume(ClaimApprovedIntegrationEvent message)
    {
        var payment = new Payment { Id = Guid.NewGuid(), ClaimId = message.ClaimId, Amount = message.Amount, Iban = message.PayeeIban };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        await bus.PublishAsync(new ClaimPaidIntegrationEvent(message.ClaimId, payment.Id));
    }
}
