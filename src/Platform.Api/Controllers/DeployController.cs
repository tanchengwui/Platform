using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using System.Security.Claims;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("environments")]
public sealed class DeployController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public DeployController(PlatformDbContext db) => _db = db;

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    [HttpGet]
    public async Task<ActionResult<List<AppEnvironment>>> List(CancellationToken ct)
        => await _db.Environments.Where(e => e.TenantId == TenantId).ToListAsync(ct);

    public sealed record DeploymentDto(
    Guid DeploymentId,
    Guid EnvId,
    Guid AppId,
    Guid AppVersionId,
    string SemVer,
    int BuildNo,
    int Attempt,
    bool CancelRequested,
    int Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? LogRef
);

    [HttpGet("{envId:guid}/apps/{appId:guid}/deployments")]
    public async Task<ActionResult<List<DeploymentDto>>> ListDeployments(Guid envId, Guid appId, CancellationToken ct)
    {
        // ensure env belongs to tenant
        var env = await _db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.EnvId == envId && e.TenantId == TenantId, ct);
        if (env is null) return NotFound("Environment not found.");

        // ensure app belongs to tenant
        var app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.AppId == appId && a.TenantId == TenantId, ct);
        if (app is null) return NotFound("App not found.");

        var list = await (
            from d in _db.Deployments.AsNoTracking()
            join v in _db.AppVersions.AsNoTracking() on d.AppVersionId equals v.AppVersionId
            where d.EnvId == envId && v.AppId == appId
            orderby d.StartedAt descending
            select new DeploymentDto(
                d.DeploymentId,
                d.EnvId,
                v.AppId,
                d.AppVersionId,
                v.SemVer,
                v.BuildNo,
                d.Attempt,
                d.CancelRequested,
                (int)d.Status,
                d.StartedAt,
                d.FinishedAt,
                d.LogRef
            )
        ).Take(50).ToListAsync(ct);

        return Ok(list);
    }

    
[HttpGet("deployments/recent")]
public async Task<ActionResult<List<DeploymentDto>>> RecentDeployments([FromQuery] int take = 50, CancellationToken ct = default)
{
    take = Math.Clamp(take, 1, 200);

    var list = await (
        from d in _db.Deployments.AsNoTracking()
        join e in _db.Environments.AsNoTracking() on d.EnvId equals e.EnvId
        join v in _db.AppVersions.AsNoTracking() on d.AppVersionId equals v.AppVersionId
        join a in _db.Apps.AsNoTracking() on v.AppId equals a.AppId
        where e.TenantId == TenantId && a.TenantId == TenantId
        orderby d.StartedAt descending
        select new DeploymentDto(
            d.DeploymentId,
            d.EnvId,
            a.AppId,
            d.AppVersionId,
            v.SemVer,
            v.BuildNo,
            d.Attempt,
            d.CancelRequested,
            (int)d.Status,
            d.StartedAt,
            d.FinishedAt,
            d.LogRef
        )
    ).Take(take).ToListAsync(ct);

    return Ok(list);
}

public sealed record DeploymentLogDto(DateTimeOffset Timestamp, string Level, string Message);

[HttpGet("deployments/{deploymentId:guid}/logs")]
public async Task<ActionResult<List<DeploymentLogDto>>> GetDeploymentLogs(Guid deploymentId, [FromQuery] int take = 200, CancellationToken ct = default)
{
    take = Math.Clamp(take, 10, 2000);

    // Ensure deployment belongs to tenant via environment
    var dep = await _db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.DeploymentId == deploymentId, ct);
    if (dep is null) return NotFound("Deployment not found.");

    var envTenant = await _db.Environments.AsNoTracking().Where(e => e.EnvId == dep.EnvId).Select(e => e.TenantId).FirstOrDefaultAsync(ct);
    if (envTenant == default || envTenant != TenantId) return Forbid();

    var logs = await _db.DeploymentLogs.AsNoTracking()
        .Where(l => l.DeploymentId == deploymentId)
        .OrderByDescending(l => l.Timestamp)
        .Take(take)
        .Select(l => new DeploymentLogDto(l.Timestamp, l.Level, l.Message))
        .ToListAsync(ct);

    logs.Reverse(); // oldest first
    return Ok(logs);
}

[Authorize(Policy = "ENV_DEPLOY")]
[HttpPost("deployments/{deploymentId:guid}/cancel")]
public async Task<ActionResult> CancelDeployment(Guid deploymentId, CancellationToken ct)
{
    var dep = await _db.Deployments.FirstOrDefaultAsync(d => d.DeploymentId == deploymentId, ct);
    if (dep is null) return NotFound("Deployment not found.");

    var env = await _db.Environments.FirstOrDefaultAsync(e => e.EnvId == dep.EnvId && e.TenantId == TenantId, ct);
    if (env is null) return Forbid();

    if (dep.Status == DeploymentStatus.Succeeded || dep.Status == DeploymentStatus.Failed || dep.Status == DeploymentStatus.Canceled)
        return BadRequest("Deployment already completed.");

    dep.CancelRequested = true;

    if (dep.Status == DeploymentStatus.Queued)
    {
        dep.Status = DeploymentStatus.Canceled;
        dep.FinishedAt = DateTimeOffset.UtcNow;
        dep.LogRef = "Canceled before start.";
    }

    _db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "WARN", Message = "Cancel requested by user." });

    _db.AuditLogs.Add(new AuditLog
    {
        TenantId = TenantId,
        ActorUserId = UserId,
        Action = "ENV_DEPLOY_CANCEL_REQUESTED",
        EntityType = "Deployment",
        EntityId = dep.DeploymentId.ToString(),
        Summary = $"Cancel requested for deployment {dep.DeploymentId}",
        CreatedAt = DateTimeOffset.UtcNow
    });

    await _db.SaveChangesAsync(ct);
    return Ok();
}

[Authorize(Policy = "ENV_DEPLOY")]
[HttpPost("deployments/{deploymentId:guid}/retry")]
public async Task<ActionResult<object>> RetryDeployment(Guid deploymentId, CancellationToken ct)
{
    var dep = await _db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.DeploymentId == deploymentId, ct);
    if (dep is null) return NotFound("Deployment not found.");

    var env = await _db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.EnvId == dep.EnvId && e.TenantId == TenantId, ct);
    if (env is null) return Forbid();

    if (dep.Status != DeploymentStatus.Failed && dep.Status != DeploymentStatus.Canceled)
        return BadRequest("Only Failed/Canceled deployments can be retried.");

    var retry = new Deployment
    {
        DeploymentId = Guid.NewGuid(),
        EnvId = dep.EnvId,
        AppVersionId = dep.AppVersionId,
        Attempt = dep.Attempt + 1,
        RetryOfDeploymentId = dep.DeploymentId,
        CancelRequested = false,
        Status = DeploymentStatus.Queued,
        RequestedBy = UserId,
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = null,
        LogRef = null
    };

    _db.Deployments.Add(retry);
    _db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = retry.DeploymentId, Level = "INFO", Message = $"Retry queued (attempt {retry.Attempt}) from {dep.DeploymentId}" });

    _db.AuditLogs.Add(new AuditLog
    {
        TenantId = TenantId,
        ActorUserId = UserId,
        Action = "ENV_DEPLOY_RETRY_QUEUED",
        EntityType = "Deployment",
        EntityId = retry.DeploymentId.ToString(),
        Summary = $"Retry queued for deployment {dep.DeploymentId} (attempt {retry.Attempt})",
        CreatedAt = DateTimeOffset.UtcNow
    });

    await _db.SaveChangesAsync(ct);

    return Ok(new { deploymentId = retry.DeploymentId, status = (int)retry.Status });
}

[Authorize(Policy = "ENV_DEPLOY")]


    [HttpPost("{envId:guid}/deploy/{appVersionId:guid}")]
    public async Task<ActionResult<object>> Deploy(Guid envId, Guid appVersionId, CancellationToken ct)
    {
        var env = await _db.Environments.FirstOrDefaultAsync(e => e.EnvId == envId && e.TenantId == TenantId, ct);
        if (env is null) return NotFound("Environment not found.");

        var ver = await _db.AppVersions.FirstOrDefaultAsync(v => v.AppVersionId == appVersionId, ct);
        if (ver is null) return NotFound("Version not found.");

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == ver.AppId && a.TenantId == TenantId, ct);
        if (app is null) return Forbid();

        // Ensure env-app mapping exists (but DON'T activate here)
        var envApp = await _db.EnvironmentApps.FirstOrDefaultAsync(x => x.EnvId == envId && x.AppId == app.AppId, ct);
        if (envApp is null)
        {
            envApp = new EnvironmentApp
            {
                EnvId = envId,
                AppId = app.AppId,
                ActiveAppVersionId = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.EnvironmentApps.Add(envApp);
        }

        var dep = new Deployment
        {
            DeploymentId = Guid.NewGuid(),
            EnvId = envId,
            AppVersionId = appVersionId,
            Status = DeploymentStatus.Queued,
            RequestedBy = UserId,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = null,
            LogRef = null
        };

        _db.Deployments.Add(dep);

        _db.DeploymentLogs.Add(new DeploymentLog { DeploymentId = dep.DeploymentId, Level = "INFO", Message = "Deployment queued." });

        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = TenantId,
            ActorUserId = UserId,
            Action = "ENV_DEPLOY_QUEUED",
            EntityType = "Deployment",
            EntityId = dep.DeploymentId.ToString(),
            Summary = $"Queued deploy {app.Key} {ver.SemVer} (build {ver.BuildNo}) to {env.Name}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        // ✅ Must match ServiceCenter DeployResponse
        var baseUrl = (env.BaseUrl ?? "").TrimEnd('/');

        // choose default page key you want to open after deploy

        var runtimeUrlHint =
            string.IsNullOrWhiteSpace(baseUrl)
                ? ""
                : $"{baseUrl}/apps/{env.EnvId}/{app.AppId}";

        return Ok(new
        {
            deploymentId = dep.DeploymentId,
            status = (int)dep.Status,
            runtimeUrlHint
        });
    }
}
