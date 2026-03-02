using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using System.Security.Claims;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("apps")]
public sealed class AppController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public AppController(PlatformDbContext db) => _db = db;

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    public sealed record CreateAppRequest(string Key, string Name);

    // POST /apps
    [Authorize(Policy = "APP_CREATE")]

    [HttpPost]
    public async Task<IActionResult> CreateApp([FromBody] CreateAppRequest req, CancellationToken ct)
    {
        var key = (req.Key ?? "").Trim();
        var name = (req.Name ?? "").Trim();

        if (string.IsNullOrWhiteSpace(key))
            return BadRequest("Key is required.");
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("Name is required.");

        var exists = await _db.Apps.AnyAsync(a => a.TenantId == TenantId && a.Key == key, ct);
        if (exists) return Conflict($"App key already exists: {key}");

        var app = new App
        {
            AppId = Guid.NewGuid(),
            TenantId = TenantId,
            Key = key,
            Name = name,
            Status = AppStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Apps.Add(app);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            appId = app.AppId,
            tenantId = app.TenantId,
            key = app.Key,
            name = app.Name
        });
    }

    // GET /apps
    [HttpGet]
    public async Task<IActionResult> GetApps(CancellationToken ct)
    {
        var apps = await _db.Apps
            .AsNoTracking()
            .Where(a => a.TenantId == TenantId)
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                appId = a.AppId,
                tenantId = a.TenantId,
                key = a.Key,
                name = a.Name,
                status = a.Status,
                createdAt = a.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(apps);
    }

    // POST /apps/{appId}/drafts
    [HttpPost("{appId:guid}/drafts")]
    public async Task<IActionResult> CreateDraft(Guid appId, CancellationToken ct)
    {
        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == appId && a.TenantId == TenantId, ct);
        if (app is null) return NotFound($"App not found: {appId}");

        var latest = await _db.AppDrafts
            .Where(d => d.AppId == appId)
            .OrderByDescending(d => d.Version)
            .Select(d => (int?)d.Version)
            .FirstOrDefaultAsync(ct);

        var draft = new AppDraft
        {
            DraftId = Guid.NewGuid(),
            AppId = appId,
            Version = (latest ?? 0) + 1,
            Status = DraftStatus.Editing,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.AppDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            draftId = draft.DraftId,
            appId = draft.AppId,
            version = draft.Version,
            status = draft.Status.ToString()
        });
    }
}
