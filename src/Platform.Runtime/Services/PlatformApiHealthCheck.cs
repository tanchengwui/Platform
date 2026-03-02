using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Runtime.Services;

/// <summary>
/// Readiness check: verifies Platform.Api is reachable.
/// Any HTTP response counts as reachable; network failure/timeouts fail readiness.
/// </summary>
public sealed class PlatformApiHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpFactory;

    public PlatformApiHealthCheck(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var http = _httpFactory.CreateClient("PlatformApi");

            // We only need to know the API is reachable. Any status code means "reachable".
            using var req = new HttpRequestMessage(HttpMethod.Head, "/");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return HealthCheckResult.Healthy("Platform.Api reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Platform.Api not reachable.", ex);
        }
    }
}
