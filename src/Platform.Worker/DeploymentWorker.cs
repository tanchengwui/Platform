using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Storage;
using System.Net.Http;

namespace Platform.Worker;

/// <summary>
/// Minimal deployment runner:
/// - Picks the oldest Queued deployment
/// - Marks Running
/// - Validates package exists for AppVersion
/// - Sets EnvironmentApps.ActiveAppVersionId = AppVersionId on success
/// - Marks Succeeded/Failed
/// </summary>
public sealed class DeploymentWorker : BackgroundService
{
    private readonly IArtifactStorage _storage;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeploymentWorker> _logger;

    public DeploymentWorker(IServiceScopeFactory scopeFactory, IArtifactStorage storage, IHttpClientFactory httpFactory, ILogger<DeploymentWorker> logger)
    {
        _storage = storage;
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProcessOneAsync(stoppingToken);
                await Task.Delay(didWork ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop error");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var dep = await db.Deployments
            .OrderBy(d => d.StartedAt)
            .FirstOrDefaultAsync(d => d.Status == DeploymentStatus.Queued, ct);

        if (dep is null) return false;

        // If cancel was requested while queued, mark canceled and skip
if (dep.CancelRequested)
{
    dep.Status = DeploymentStatus.Canceled;
    dep.FinishedAt = DateTimeOffset.UtcNow;
    dep.LogRef = "Canceled before start.";
    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "WARN", Message = "Canceled before start." });
    await db.SaveChangesAsync(ct);
    return true;
}

dep.Status = DeploymentStatus.Running;
if (dep.StartedAt == default)
    dep.StartedAt = DateTimeOffset.UtcNow;

db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Started deployment." });
await db.SaveChangesAsync(ct);

        try
        {
            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Loading version metadata..." });
            await db.SaveChangesAsync(ct);

            var ver = await db.AppVersions.FirstOrDefaultAsync(v => v.AppVersionId == dep.AppVersionId, ct)
                      ?? throw new InvalidOperationException("AppVersion not found.");

            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Locating package..." });
            await db.SaveChangesAsync(ct);

            var pkg = await db.Packages.FirstOrDefaultAsync(p => p.AppVersionId == dep.AppVersionId, ct)
                      ?? throw new InvalidOperationException("No package for this AppVersion.");

            if (dep.CancelRequested) throw new OperationCanceledException("Cancel requested.");

            if (string.IsNullOrWhiteSpace(pkg.StoragePath) || !await _storage.ExistsAsync(pkg.StoragePath, ct))
                throw new InvalidOperationException("Package file missing on API server disk.");

            db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Updating environment mapping..." });
            await db.SaveChangesAsync(ct);

            var envApp = await db.EnvironmentApps.FirstOrDefaultAsync(x => x.EnvId == dep.EnvId && x.AppId == ver.AppId, ct);
            if (envApp is null)
            {
                envApp = new EnvironmentApp { EnvId = dep.EnvId, AppId = ver.AppId };
                db.EnvironmentApps.Add(envApp);
            }

            envApp.ActiveAppVersionId = dep.AppVersionId;
            envApp.UpdatedAt = DateTimeOffset.UtcNow;

            // Pull + materialize runtime HTML for wow-factor
db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Syncing runtime (pull-active)..." });
await db.SaveChangesAsync(ct);

var env = await db.Environments.AsNoTracking().FirstAsync(e => e.EnvId == dep.EnvId, ct);
var runtimeBase = (env.BaseUrl ?? "").TrimEnd('/');

if (!string.IsNullOrWhiteSpace(runtimeBase))
{
    var client = _httpFactory.CreateClient("RuntimeSync");

    // Optional shared key (matches Platform.Runtime InternalSyncKey)
    var syncKey = Environment.GetEnvironmentVariable("PLATFORM_INTERNAL_SYNC_KEY");
    if (!string.IsNullOrWhiteSpace(syncKey))
    {
        client.DefaultRequestHeaders.Remove("X-Platform-Internal-Key");
        client.DefaultRequestHeaders.Add("X-Platform-Internal-Key", syncKey);
    }

    var syncUrl = $"{runtimeBase}/sync/pull-active?envId={dep.EnvId}&appId={ver.AppId}";
    using var syncRes = await client.PostAsync(syncUrl, content: null, ct);

    if (!syncRes.IsSuccessStatusCode)
    {
        var body = await syncRes.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Runtime sync failed: {(int)syncRes.StatusCode} {syncRes.ReasonPhrase} {body}");
    }
}
else
{
    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "WARN", Message = "Environment BaseUrl is empty. Skipping runtime sync." });
    await db.SaveChangesAsync(ct);
}

db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Deployment succeeded. Activating version." });

            dep.Status = DeploymentStatus.Succeeded;
            dep.FinishedAt = DateTimeOffset.UtcNow;

            db.AuditLogs.Add(new AuditLog
            {
                TenantId = (await db.Environments.AsNoTracking().Where(e => e.EnvId == dep.EnvId).Select(e => e.TenantId).FirstAsync(ct)),
                ActorUserId = dep.RequestedBy,
                Action = "ENV_DEPLOY_SUCCEEDED",
                EntityType = "Deployment",
                EntityId = dep.DeploymentId.ToString(),
                Summary = $"Deployment succeeded. ActiveAppVersionId = {dep.AppVersionId}",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException ocex)
{
    dep.Status = DeploymentStatus.Canceled;
    dep.FinishedAt = DateTimeOffset.UtcNow;
    dep.LogRef = ocex.Message;

    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "WARN", Message = $"Canceled: {ocex.Message}" });

    _logger.LogWarning(ocex, "Deployment canceled: {DeploymentId}", dep.DeploymentId);
    await db.SaveChangesAsync(ct);
}
catch (Exception ex)
{
    dep.Status = DeploymentStatus.Failed;
    dep.FinishedAt = DateTimeOffset.UtcNow;
    dep.LogRef = ex.Message;

    db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "ERROR", Message = ex.Message });

    _logger.LogWarning(ex, "Deployment failed: {DeploymentId}", dep.DeploymentId);
    await db.SaveChangesAsync(ct);
}


        return true;
    }
}
