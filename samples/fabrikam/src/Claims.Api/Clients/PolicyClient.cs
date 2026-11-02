using System.Net.Http.Json;

namespace Fabrikam.Claims.Api.Clients;

public sealed class PolicyClient(HttpClient httpClient)
{
    private const string BasePath = "api/policies/";

    public Task<CoverageDto?> GetCoverageAsync(string policyNumber) =>
        httpClient.GetFromJsonAsync<CoverageDto>($"{BasePath}{policyNumber}/coverage");
}

public sealed record CoverageDto(string PolicyNumber, bool Active, decimal RemainingLimit);
