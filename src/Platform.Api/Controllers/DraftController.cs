using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using System.Security.Claims;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("drafts")]
public sealed class DraftController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public DraftController(PlatformDbContext db) => _db = db;

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    [Authorize(Policy = "APP_PUBLISH")]


    [HttpPut("{draftId:guid}/resources/{type}/{key}")]
    public async Task<IActionResult> UpsertResource(Guid draftId, string type, string key, [FromBody] object payload)
    {
        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == draftId);
        if (draft is null) return NotFound();

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == TenantId);
        if (app is null) return Forbid();

        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        var existing = await _db.DraftResources.FirstOrDefaultAsync(r => r.DraftId == draftId && r.Type == type && r.Key == key);
        if (existing is null)
        {
            _db.DraftResources.Add(new DraftResource { DraftId = draftId, Type = type, Key = key, JsonPayload = json });
        }
        else
        {
            existing.JsonPayload = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        draft.UpdatedAt = DateTimeOffset.UtcNow;

        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = TenantId,
            ActorUserId = UserId,
            Action = "DRAFT_UPSERT_RESOURCE",
            EntityType = "DraftResource",
            EntityId = $"{draftId}:{type}:{key}",
            Summary = $"Upserted {type}/{key}"
        });

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Policy = "APP_PUBLISH")]


    [HttpGet("{draftId:guid}/resources/{type}/{key}")]
    public async Task<ActionResult<object>> GetResource(Guid draftId, string type, string key)
    {
        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == draftId);
        if (draft is null) return NotFound();

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == TenantId);
        if (app is null) return Forbid();

        var res = await _db.DraftResources.FirstOrDefaultAsync(r => r.DraftId == draftId && r.Type == type && r.Key == key);
        if (res is null) return NotFound();

        return Content(res.JsonPayload, "application/json");
    }
}