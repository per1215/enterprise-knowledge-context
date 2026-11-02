using Fabrikam.Claims.Api.Application;
using Fabrikam.Claims.Api.Clients;
using Fabrikam.Claims.Api.IntegrationEvents;
using Fabrikam.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<ClaimValidator>();
builder.Services.AddHttpClient<PolicyClient>(client => client.BaseAddress = new("https+http://policy-api"));
builder.Services.AddEventBus()
    .AddSubscription<ClaimFraudCheckedIntegrationEvent, ClaimFraudCheckedHandler>();

var app = builder.Build();
app.MapControllers();
app.Run();
