using System.Net.Http.Json;

namespace Platform.ServiceCenter.Services;

public sealed class PlatformApiClient
{
    private readonly HttpClient _http;

    public PlatformApiClient(HttpClient http) => _http = http;

    public async Task<List<AppSummaryDto>> GetAppsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AppSummaryDto>>("/apps", ct) ?? new();

    public async Task PublishAsync(string appId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/apps/{appId}/drafts", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<EnvironmentDto>> GetEnvironmentsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<EnvironmentDto>>("/environments", ct) ?? new();

    public async Task DeployAsync(Guid envId, Guid appVersionId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/environments/{envId}/deploy/{appVersionId}", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ✅ NEST DTOs INSIDE THE CLASS
    public sealed record EnvironmentDto(
        Guid EnvId,
        Guid TenantId,
        string Name,
        int Type,
        string BaseUrl,
        DateTimeOffset CreatedAt
    );
}

public sealed record AppSummaryDto(
    Guid AppId,
    Guid TenantId,
    string Key,
    string Name,
    int Status,
    DateTimeOffset CreatedAt
);