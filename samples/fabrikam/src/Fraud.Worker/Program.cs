using Fabrikam.Contracts;
using Fabrikam.Fraud.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<FraudScorer>();
builder.Services.AddEventBus()
    .AddSubscription<ClaimSubmittedIntegrationEvent, ClaimSubmittedHandler>();
builder.Build().Run();
