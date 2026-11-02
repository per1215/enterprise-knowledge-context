using Fabrikam.Contracts;
using Fabrikam.Notifications.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEventBus()
    .AddSubscription<ClaimPaidIntegrationEvent, ClaimPaidHandler>();
builder.Build().Run();
