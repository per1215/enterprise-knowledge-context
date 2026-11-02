using Fabrikam.Payments.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<PaymentsDbContext>();

var app = builder.Build();

app.MapGet("/api/payments/{claimId:guid}", async (Guid claimId, PaymentsDbContext db) =>
    await db.Payments.Where(p => p.ClaimId == claimId).ToListAsync());

app.Run();
