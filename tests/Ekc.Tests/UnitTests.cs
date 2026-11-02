using Ekc.Extractors.Common;
using Ekc.Extractors.DotNet;

namespace Ekc.Tests;

public sealed class UnitTests
{
    [Theory]
    [InlineData("api/catalog/items/{id:int}", "/api/catalog/items/{}")]
    [InlineData("/API/Orders/", "/api/orders")]
    [InlineData("items/by?ids={x}", "/items/by")]
    [InlineData("//a//b", "/a/b")]
    public void Paths_normalise_for_matching(string input, string expected) => Assert.Equal(expected, NodeIds.NormalizePath(input));

    [Fact]
    public void Route_matching_prefers_exact_segments_and_parameters()
    {
        var call = NodeIds.NormalizePath("api/catalog/items/{}");

        Assert.True(RouteMatcher.Score(call, "/api/catalog/items/{}") > RouteMatcher.Score(call, "/api/catalog/items/by"));
        Assert.Equal(-1, RouteMatcher.Score(call, "/api/catalog/items"));
        Assert.Equal(-1, RouteMatcher.Score("/a/b", "/a/c"));
    }

    [Theory]
    [InlineData("https+http://catalog-api", "catalog-api", "")]
    [InlineData("http://ordering-api:8080/api/", "ordering-api", "api")]
    [InlineData("https://api.github.com/repos", "api.github.com", "repos")]
    public void Service_urls_parse(string url, string service, string basePath) =>
        Assert.Equal((service, basePath), HttpClientExtractor.ParseServiceUrl(url));

    [Fact]
    public void Non_urls_are_not_services() => Assert.Null(HttpClientExtractor.ParseServiceUrl("api/catalog"));

    [Theory]
    [InlineData("/src/Claims.Api/", "src/Claims.Api/Claims.Api.csproj", true)]
    [InlineData("/src/Claims.Api/", "src/Claims.Api.Tests/x.csproj", false)]
    [InlineData("*.csproj", "deep/dir/A.csproj", true)]
    [InlineData("src/**/Api/", "src/a/b/Api/A.csproj", true)]
    public void Codeowners_patterns_match_like_git(string pattern, string path, bool expected) =>
        Assert.Equal(expected, CodeOwnersExtractor.ToRegex(pattern).IsMatch(path));

    [Theory]
    [InlineData("Catalog.API", "catalog-api")]
    [InlineData("OrderProcessor", "order-processor")]
    [InlineData("Portal.Web", "portal-web")]
    public void Project_names_become_service_names(string project, string expected) => Assert.Equal(expected, ServiceDiscovery.KebabCase(project));

    [Fact]
    public void Diff_parser_handles_pure_deletions_and_multiple_hunks()
    {
        var files = Ekc.Analysis.DiffMapper.Parse("+++ b/a.cs\n@@ -5,2 +4,0 @@\n@@ -20 +19,3 @@\n+++ /dev/null\n@@ -1 +0,0 @@\n");

        var file = Assert.Single(files);
        Assert.Equal([(4, 4), (19, 21)], file.Ranges);
    }

    [Fact]
    public void Resolver_understands_qualified_and_unqualified_references()
    {
        var graph = new SystemGraph();
        graph.AddNode(NodeIds.Service("orders"), NodeKind.Service, "orders");
        var endpoint = graph.AddNode(NodeIds.Endpoint("orders", "GET", "/api/orders/{id}"), NodeKind.Endpoint, "GET /api/orders/{id}", "orders");
        endpoint.Properties["verb"] = "GET";
        endpoint.Properties["normalizedPath"] = "/api/orders/{}";
        graph.AddNode(NodeIds.Topic("OrderPlaced"), NodeKind.Topic, "OrderPlaced");

        Assert.Same(endpoint, Assert.Single(GraphResolver.Resolve(graph, "GET /api/orders/42")));
        Assert.Same(endpoint, Assert.Single(GraphResolver.Resolve(graph, "orders: GET /api/orders/{orderId:int}")));
        Assert.Empty(GraphResolver.Resolve(graph, "billing: GET /api/orders/42"));
        Assert.Equal("OrderPlaced", Assert.Single(GraphResolver.Resolve(graph, "topic: orderplaced")).Name);
        Assert.Equal("orders", Assert.Single(GraphResolver.Resolve(graph, "orders")).Name);
    }
}
