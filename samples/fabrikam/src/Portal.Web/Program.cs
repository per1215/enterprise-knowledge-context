using Fabrikam.Portal.Web.Clients;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<ClaimsClient>(client => client.BaseAddress = new("https+http://claims-api"));

var app = builder.Build();

app.MapPost("/claims", async (NewClaimForm form, ClaimsClient claims) =>
    Results.Ok(await claims.SubmitAsync(form.PolicyNumber, form.Amount, form.Description)));

app.MapGet("/claims/{id}", async (Guid id, ClaimsClient claims) => Results.Ok(await claims.GetAsync(id)));

app.Run();

public sealed record NewClaimForm(string PolicyNumber, decimal Amount, string Description);
