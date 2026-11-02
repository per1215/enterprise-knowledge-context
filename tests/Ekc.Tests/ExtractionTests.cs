using Ekc.Analysis;

namespace Ekc.Tests;

/// <summary>What the extractors find in the Fabrikam estate. Each assertion is a fact visible in the sample source.</summary>
[Collection(FabrikamCollection.Name)]
public sealed class ExtractionTests(FabrikamFixture f)
{
    [Fact]
    public void Services_come_from_the_Aspire_AppHost()
    {
        var services = f.Graph.OfKind(NodeKind.Service).Select(s => s.Name).Order().ToList();

        Assert.Equal(["claims-api", "fraud-worker", "notifications-worker", "payments-api", "policy-api", "portal-web"], services);
        Assert.All(f.Graph.OfKind(NodeKind.Service), s => Assert.Equal("aspire", s.Properties["discoveredBy"]));
    }

    [Fact]
    public void Infrastructure_and_orchestration_dependencies_are_captured()
    {
        Assert.True(f.HasEdge("service:claims-api", EdgeKind.UsesResource, "resource:claimsdb"));
        Assert.True(f.HasEdge("service:claims-api", EdgeKind.UsesResource, "resource:messaging"));
        Assert.True(f.HasEdge("service:claims-api", EdgeKind.DependsOn, "service:policy-api"));
        Assert.True(f.HasEdge("service:portal-web", EdgeKind.DependsOn, "service:claims-api"));
        Assert.Equal("postgres", f.Graph.Find("resource:claimsdb")!.Properties["parent"]);
    }

    [Theory]
    [InlineData("claims-api: POST /api/claims", "ClaimsController.Submit")]
    [InlineData("claims-api: GET /api/claims/{id}", "ClaimsController.Get")]
    [InlineData("claims-api: POST /api/claims/{id}/approve", "ClaimsController.Approve")]
    [InlineData("policy-api: GET /api/policies/{policyNumber}/coverage", "Program.GetCoverage")]
    [InlineData("payments-api: GET /api/payments/{claimId}", "lambda@")]
    [InlineData("portal-web: POST /claims", "lambda@")]
    public void Endpoints_from_controllers_route_groups_and_lambdas_have_handlers(string endpoint, string handler)
    {
        var node = f.Single(endpoint);
        var handlers = f.Graph.Outgoing(node.Id, EdgeKind.ImplementedBy).Select(e => f.Graph.Find(e.To)!.Name).ToList();

        Assert.Contains(handlers, h => h.Contains(handler, StringComparison.Ordinal));
    }

    [Fact]
    public void Typed_http_clients_resolve_to_the_called_endpoint()
    {
        var submit = f.Single("ClaimsClient.SubmitAsync");
        var coverage = f.Single("PolicyClient.GetCoverageAsync");

        Assert.True(f.HasEdge(submit.Id, EdgeKind.CallsEndpoint, f.Single("claims-api: POST /api/claims").Id));
        Assert.True(f.HasEdge(coverage.Id, EdgeKind.CallsEndpoint, f.Single("policy-api: GET /api/policies/{x}/coverage").Id));
        Assert.True(f.HasEdge(f.Single("ClaimsClient.GetAsync").Id, EdgeKind.CallsEndpoint, f.Single("claims-api: GET /api/claims/{id}").Id));
    }

    [Theory]
    [InlineData("ClaimSubmittedIntegrationEvent", "claims-api", "fraud-worker")]
    [InlineData("ClaimFraudCheckedIntegrationEvent", "fraud-worker", "claims-api")]
    [InlineData("ClaimApprovedIntegrationEvent", "claims-api", "payments-api")]
    [InlineData("ClaimPaidIntegrationEvent", "payments-api", "notifications-worker")]
    public void Integration_events_link_publishers_to_subscribers(string topic, string publisher, string subscriber)
    {
        var topicId = NodeIds.Topic(topic);
        var publishers = f.Graph.Incoming(topicId, EdgeKind.Publishes).Select(e => f.Graph.Find(e.From)!.Service);
        var subscribers = f.Graph.Incoming(topicId, EdgeKind.Subscribes).Select(e => f.Graph.Find(e.From)!.Name);

        Assert.Contains(publisher, publishers);
        Assert.Contains(subscriber, subscribers);
        Assert.NotEmpty(f.Graph.Outgoing(topicId, EdgeKind.HandledBy));
    }

    [Fact]
    public void Commands_dispatch_to_their_in_process_handler()
    {
        var approve = f.Single("ClaimsController.Approve");
        var handler = f.Single("ApproveClaimCommandHandler.Handle");

        var edge = Assert.Single(f.Graph.Outgoing(approve.Id, EdgeKind.Dispatches));
        Assert.Equal(handler.Id, edge.To);
        Assert.Equal("ApproveClaimCommand", edge.Properties["message"]);
    }

    [Fact]
    public void Tables_use_ToTable_names_and_reads_and_writes_are_distinguished()
    {
        Assert.Equal("claims.claims", f.Graph.Find("table:ClaimsDbContext.Claim")!.Name);
        Assert.Equal("policy.policies", f.Graph.Find("table:PolicyDbContext.InsurancePolicy")!.Name);
        Assert.Equal("finance.payments", f.Graph.Find("table:PaymentsDbContext.Payment")!.Name);

        Assert.True(f.HasEdge(f.Single("ClaimsController.Submit").Id, EdgeKind.WritesTable, "table:ClaimsDbContext.Claim"));
        Assert.True(f.HasEdge(f.Single("Program.GetCoverage").Id, EdgeKind.ReadsTable, "table:PolicyDbContext.InsurancePolicy"));
        Assert.True(f.HasEdge("table:PaymentsDbContext.Payment", EdgeKind.StoredIn, "resource:paymentsdb"));
    }

    [Fact]
    public void Calls_resolve_through_typed_fields_and_lambda_parameters()
    {
        Assert.True(f.HasEdge(f.Single("ClaimsController.Submit").Id, EdgeKind.Invokes, f.Single("ClaimValidator.EnsureWithinCoverage").Id));
        Assert.True(f.HasEdge(f.Single("ClaimValidator.EnsureWithinCoverage").Id, EdgeKind.Invokes, f.Single("PolicyClient.GetCoverageAsync").Id));

        var portalLambda = f.Graph.Outgoing(f.Single("portal-web: POST /claims").Id, EdgeKind.ImplementedBy).Single().To;
        Assert.True(f.HasEdge(portalLambda, EdgeKind.Invokes, f.Single("ClaimsClient.SubmitAsync").Id));
    }

    [Fact]
    public void Business_rules_come_from_attributes_comments_and_the_overlay()
    {
        var rules = f.Graph.OfKind(NodeKind.BusinessRule).ToDictionary(r => r.Name, r => f.Graph.Outgoing(r.Id, EdgeKind.EnforcedBy).Select(e => f.Graph.Find(e.To)!.Name).ToList());

        Assert.Equal(["ClaimValidator.EnsureWithinCoverage"], rules["Claim amount cannot exceed policy coverage"]);
        Assert.Equal(["Claim.Approve"], rules["Claims above 10,000 need senior approval"]);
        Assert.Equal(["FraudScorer.Score"], rules["Fraud score above 0.8 rejects the claim"]);
    }

    [Fact]
    public void Owners_domains_and_processes_are_linked_without_warnings()
    {
        Assert.Empty(f.Graph.Warnings);
        Assert.Equal(["fabrikam/claims-team", "fabrikam/risk"], f.Graph.OwnersOf(["fraud-worker"]));
        Assert.True(f.HasEdge("service:policy-api", EdgeKind.PartOfDomain, NodeIds.Domain("Policy Administration")));

        var steps = f.Graph.Outgoing(NodeIds.Process("Submit a claim"), EdgeKind.ProcessStep)
            .OrderBy(e => int.Parse(e.Properties["order"]))
            .Select(e => f.Graph.Find(e.To)!.Name)
            .ToList();
        Assert.Equal(["POST /claims", "POST /api/Claims", "ClaimSubmittedIntegrationEvent", "ClaimFraudCheckedIntegrationEvent"], steps);
    }

    [Fact]
    public async Task The_graph_round_trips_through_the_json_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ekc-{Guid.NewGuid():N}.json");
        try
        {
            await GraphStore.SaveAsync(f.Graph, path, TestContext.Current.CancellationToken);
            var loaded = await GraphStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(f.Graph.Nodes.Count, loaded.Nodes.Count);
            Assert.Equal(f.Graph.Edges.Count, loaded.Edges.Count);
            Assert.Equal(f.Graph.Outgoing("service:claims-api").Count, loaded.Outgoing("service:claims-api").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
