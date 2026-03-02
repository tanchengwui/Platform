using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using System.Security.Claims;
using System.Text.Json;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("drafts/{draftId:guid}/entities")]
public sealed class DraftEntitiesController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public DraftEntitiesController(PlatformDbContext db) => _db = db;

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    public sealed record EntityField(string Name, string Type = "string", bool Required = false, int? Length = null);
    public sealed record EntityDef(string Name, List<EntityField> Fields);

    [HttpGet]
    public async Task<ActionResult<List<EntityDef>>> List(Guid draftId, CancellationToken ct)
    {
        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == draftId, ct);
        if (draft is null) return NotFound();

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == TenantId, ct);
        if (app is null) return Forbid();

        var list = await _db.DraftResources
            .Where(r => r.DraftId == draftId && r.Type == "ENTITY")
            .Select(r => new { r.Key, r.JsonPayload })
            .ToListAsync(ct);

        var result = new List<EntityDef>();
        foreach (var r in list)
        {
            try
            {
                var def = JsonSerializer.Deserialize<EntityDef>(r.JsonPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (def is not null) result.Add(def);
            }
            catch { /* ignore */ }
        }

        return result;
    }

    [HttpPut("{entityName}")]
    public async Task<IActionResult> Upsert(Guid draftId, string entityName, [FromBody] EntityDef def, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityName)) return BadRequest("Missing entity name.");
        if (!string.Equals(entityName, def.Name, StringComparison.OrdinalIgnoreCase))
            def = def with { Name = entityName };

        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == draftId, ct);
        if (draft is null) return NotFound();

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == TenantId, ct);
        if (app is null) return Forbid();

        // Basic validation
        foreach (var f in def.Fields)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) return BadRequest("Field name cannot be empty.");
        }

        var json = JsonSerializer.Serialize(def, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var existing = await _db.DraftResources.FirstOrDefaultAsync(r => r.DraftId == draftId && r.Type == "ENTITY" && r.Key == entityName, ct);
        if (existing is null)
        {
            _db.DraftResources.Add(new DraftResource { DraftId = draftId, Type = "ENTITY", Key = entityName, JsonPayload = json });
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
            Action = "DRAFT_UPSERT_ENTITY",
            EntityType = "DraftResource",
            EntityId = $"{draftId}:ENTITY:{entityName}",
            Summary = $"Upserted ENTITY/{entityName}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Creates 3 pages:
    //   <Entity>_List   kind=CRUD_LIST
    //   <Entity>_Create kind=CRUD_CREATE
    //   <Entity>_Edit   kind=CRUD_EDIT
    [Authorize(Policy = "APP_PUBLISH")]
    [HttpPost("{entityName}/generate-crud")]
    public async Task<IActionResult> GenerateCrud(Guid draftId, string entityName, CancellationToken ct)
    {
        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == draftId, ct);
        if (draft is null) return NotFound();

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == TenantId, ct);
        if (app is null) return Forbid();

        var entityRes = await _db.DraftResources.FirstOrDefaultAsync(r => r.DraftId == draftId && r.Type == "ENTITY" && r.Key == entityName, ct);
        if (entityRes is null) return NotFound("Entity definition not found. Save entity first.");

        var def = JsonSerializer.Deserialize<EntityDef>(entityRes.JsonPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (def is null) return BadRequest("Invalid entity payload.");

        var columns = def.Fields.Select(f => new { name = f.Name, type = f.Type }).ToList();
        var fields = def.Fields.Select(f => new { name = f.Name, type = f.Type, required = f.Required }).ToList();

        async Task UpsertPage(string key, object payload)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var existing = await _db.DraftResources.FirstOrDefaultAsync(r => r.DraftId == draftId && r.Type == "UI_PAGE" && r.Key == key, ct);
            if (existing is null)
                _db.DraftResources.Add(new DraftResource { DraftId = draftId, Type = "UI_PAGE", Key = key, JsonPayload = json });
            else
            {
                existing.JsonPayload = json;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await UpsertPage($"{entityName}_List", new
        {
            kind = "CRUD_LIST",
            title = $"{entityName} List",
            entity = entityName,
            columns
        });

        await UpsertPage($"{entityName}_Create", new
        {
            kind = "CRUD_CREATE",
            title = $"{entityName} Create",
            entity = entityName,
            fields
        });

        await UpsertPage($"{entityName}_Edit", new
        {
            kind = "CRUD_EDIT",
            title = $"{entityName} Edit",
            entity = entityName,
            fields
        });

        draft.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
