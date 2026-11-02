using Fabrikam.Claims.Api.Application;
using Fabrikam.Claims.Api.Data;
using Fabrikam.Claims.Api.Domain;
using Fabrikam.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Fabrikam.Claims.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ClaimsController(ClaimsDbContext db, ClaimValidator validator, IEventBus bus, IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Submit(SubmitClaimRequest request)
    {
        await validator.EnsureWithinCoverage(request.PolicyNumber, request.Amount);

        var claim = Claim.Open(request.PolicyNumber, request.Amount, request.Description);
        db.Claims.Add(claim);
        await db.SaveChangesAsync();

        await bus.PublishAsync(new ClaimSubmittedIntegrationEvent(claim.Id, claim.PolicyNumber, claim.Amount));
        return Accepted(new { claim.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var claim = await db.Claims.FindAsync(id);
        return claim is null ? NotFound() : Ok(claim);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, ApproveClaimRequest request)
    {
        var approved = await mediator.Send(new ApproveClaimCommand(id, request.ApprovedBy, request.PayeeIban));
        return approved ? NoContent() : Conflict();
    }
}

public sealed record SubmitClaimRequest(string PolicyNumber, decimal Amount, string Description);

public sealed record ApproveClaimRequest(string ApprovedBy, string PayeeIban);
