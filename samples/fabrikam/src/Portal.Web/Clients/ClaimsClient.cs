using System.Net.Http.Json;

namespace Fabrikam.Portal.Web.Clients;

public sealed class ClaimsClient(HttpClient httpClient)
{
    public async Task<string> SubmitAsync(string policyNumber, decimal amount, string description)
    {
        var response = await httpClient.PostAsJsonAsync("api/claims", new { policyNumber, amount, description });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public Task<string> GetAsync(Guid id) => httpClient.GetStringAsync($"api/claims/{id}");
}
