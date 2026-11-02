using Ekc.Analysis;
using Ekc.Cli;

namespace Ekc.Tests;

[Collection(FabrikamCollection.Name)]
public sealed class AnalysisTests(FabrikamFixture f)
{
    [Fact]
    public void Submit_claim_flow_crosses_services_in_execution_order()
    {
        var flow = new FlowTracer(f.Graph).Trace([f.Single("portal-web: POST /claims")]);
        var significant = flow.Steps.Where(s => s.Kind is FlowStepKind.HttpCall or FlowStepKind.Publish or FlowStepKind.Consume or FlowStepKind.DataWrite)
            .Select(s => $"{s.Kind}:{s.Service}")
            .ToList();

        Assert.Equal(
        [
            "HttpCall:portal-web",       // portal → claims-api POST /api/claims
            "HttpCall:claims-api",       // claims → policy-api coverage
            "DataWrite:claims-api",      // insert claim
            "Publish:claims-api",        // ClaimSubmitted
            "Consume:fraud-worker",
            "Publish:fraud-worker",      // ClaimFraudChecked
            "Consume:claims-api",
            "DataWrite:claims-api",      // status update
        ], significant);
        Assert.All(flow.Steps.Where(s => s.Kind != FlowStepKind.Entry), s => Assert.NotNull(s.Evidence));
    }

    [Fact]
    public void A_business_process_expands_to_its_steps()
    {
        var flow = new FlowTracer(f.Graph).Trace([f.Single("process: Approve and pay a claim")]);

        Assert.Contains(flow.Steps, s => s.Kind == FlowStepKind.Dispatch && s.Description.Contains("ApproveClaimCommand", StringComparison.Ordinal));
        Assert.Contains(flow.Steps, s => s.Kind == FlowStepKind.Consume && s.Service == "notifications-worker");
    }

    [Fact]
    public void Changing_a_validation_rule_is_high_risk_and_names_callers_rules_processes_and_owners()
    {
        var report = new ImpactAnalyzer(f.Graph).Analyze([f.Single("ClaimValidator.EnsureWithinCoverage")]);

        Assert.Equal(RiskLevel.High, report.Risk);
        Assert.Contains(report.Endpoints, e => e.Name == "POST /api/Claims");
        Assert.Contains(report.Callers, c => c.Service == "portal-web" && c.Name == "POST /claims");
        Assert.Contains(report.Rules, r => r.Name == "Claim amount cannot exceed policy coverage");
        Assert.Contains(report.Processes, p => p.Name == "Submit a claim");
        Assert.Equal(["fabrikam/claims-team", "fabrikam/digital-channels"], report.Teams);
    }

    [Fact]
    public void Changing_an_event_publisher_reaches_the_consumers()
    {
        var report = new ImpactAnalyzer(f.Graph).Analyze([f.Single("ClaimApprovedConsumer.Consume")]);

        Assert.Contains(report.Topics, t => t.Name == "ClaimPaidIntegrationEvent");
        Assert.Contains(report.EventConsumers, c => c.Service == "notifications-worker");
        Assert.Contains(report.Tables, t => t.Name == "finance.payments");
        Assert.Contains(report.DataConsumers, d => d.Service == "payments-api" && d.Name.Contains("lambda@", StringComparison.Ordinal));
        Assert.Contains("notifications-worker", report.Services);
    }

    [Fact]
    public void Changing_a_downstream_endpoint_reaches_callers_two_services_away()
    {
        var report = new ImpactAnalyzer(f.Graph).Analyze([f.Single("Program.GetCoverage")]);

        Assert.Contains(report.Callers, c => c.Service == "claims-api" && c.Name == "PolicyClient.GetCoverageAsync");
        Assert.Contains(report.Callers, c => c.Service == "claims-api" && c.Name == "POST /api/Claims");
    }

    [Fact]
    public void A_private_helper_with_no_callers_is_low_risk()
    {
        var report = new ImpactAnalyzer(f.Graph).Analyze([f.Single("Claim.MarkFraudChecked")]);

        Assert.NotEqual(RiskLevel.High, report.Risk);
        Assert.DoesNotContain(report.Callers, c => c.Service != "claims-api");
    }

    [Fact]
    public void Git_diffs_map_to_the_changed_methods()
    {
        const string diff = """
            diff --git a/src/Claims.Api/Application/ClaimValidator.cs b/src/Claims.Api/Application/ClaimValidator.cs
            --- a/src/Claims.Api/Application/ClaimValidator.cs
            +++ b/src/Claims.Api/Application/ClaimValidator.cs
            @@ -17 +17 @@ public sealed class ClaimValidator(PolicyClient policies)
            -        if (amount > coverage.RemainingLimit)
            +        if (amount >= coverage.RemainingLimit)
            diff --git a/README.md b/README.md
            --- a/README.md
            +++ b/README.md
            @@ -1,0 +2,2 @@
            +text
            """;

        var (methods, unmapped) = DiffMapper.Map(f.Graph, DiffMapper.Parse(diff));

        Assert.Equal("ClaimValidator.EnsureWithinCoverage", Assert.Single(methods).Name);
        Assert.Equal(["README.md"], unmapped);
    }

    [Fact]
    public void Pull_request_markdown_has_marker_risk_and_owners()
    {
        var report = new ImpactAnalyzer(f.Graph).Analyze([f.Single("ClaimValidator.EnsureWithinCoverage")]);
        var markdown = MarkdownRenderer.Impact(report, forPullRequest: true);

        Assert.StartsWith(MarkdownRenderer.PrCommentMarker, markdown, StringComparison.Ordinal);
        Assert.Contains("High risk", markdown, StringComparison.Ordinal);
        Assert.Contains("@fabrikam/digital-channels", markdown, StringComparison.Ordinal);
        Assert.Contains("src/Claims.Api/Application/ClaimValidator.cs:7", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagrams_include_services_events_and_databases()
    {
        var mermaid = DiagramExporter.MermaidContainers(f.Graph);
        var structurizr = DiagramExporter.Structurizr(f.Graph, "Fabrikam");

        Assert.Contains("claims_api -.->|\"ClaimSubmitted\"| fraud_worker", mermaid, StringComparison.Ordinal);
        Assert.Contains("portal_web -->|\"HTTP\"| claims_api", mermaid, StringComparison.Ordinal);
        Assert.Contains("claimsdb[(\"claimsdb\")]", mermaid, StringComparison.Ordinal);
        Assert.Contains("workspace \"Fabrikam\"", structurizr, StringComparison.Ordinal);
        Assert.Contains("claims_api -> policy_api \"HTTP\"", structurizr, StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_tools_answer_with_evidence()
    {
        var tools = new McpTools(new GraphSession(f.Graph));

        Assert.Contains("| claims-api |", tools.ListServices(), StringComparison.Ordinal);
        Assert.Contains("Subscribes to", tools.DescribeService("claims-api"), StringComparison.Ordinal);
        Assert.Contains("fraud-worker consumes ClaimSubmittedIntegrationEvent", tools.TraceFlow("POST /api/claims"), StringComparison.Ordinal);
        Assert.Contains("sequenceDiagram", tools.TraceFlow("Submit a claim", includeDiagram: true), StringComparison.Ordinal);
        Assert.Contains("High risk", tools.ImpactOfChange("src/Claims.Api/Application/ClaimValidator.cs:12"), StringComparison.Ordinal);
        Assert.Contains("@fabrikam/policy-team", tools.FindOwners("table: policy.policies"), StringComparison.Ordinal);
        Assert.Contains("Known services", tools.DescribeService("does-not-exist"), StringComparison.Ordinal);
    }
}
