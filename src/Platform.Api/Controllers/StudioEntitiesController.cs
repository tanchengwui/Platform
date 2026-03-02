using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using System.Security.Claims;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize]
[Route("studio/apps/{appId:guid}/entities")]
public sealed class StudioEntitiesController : ControllerBase
{
    private readonly PlatformDbContext _db;
    public StudioEntitiesController(PlatformDbContext db) => _db = db;

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);

    [HttpGet]
    public async Task<ActionResult<object>> List(Guid appId, CancellationToken ct)
    {
        var items = await _db.StudioEntities
            .AsNoTracking()
            .Where(e => e.TenantId == TenantId && e.AppId == appId)
            .OrderBy(e => e.Name)
            .ToListAsync(ct);

        return Ok(items.Select(e => new { e.EntityId, e.Name, e.TableName, e.UpdatedAt }));
    }

    public sealed record UpsertEntityRequest(string Name);

    [HttpPost]
    public async Task<ActionResult<object>> Create(Guid appId, [FromBody] UpsertEntityRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length < 1) return BadRequest("Name is required.");

        var entity = new StudioEntity
        {
            TenantId = TenantId,
            AppId = appId,
            Name = name,
            TableName = $"AppData_{appId:N}_{MakeSafe(name)}",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.StudioEntities.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(new { entity.EntityId, entity.Name, entity.TableName });
    }

    [HttpGet("{entityId:guid}")]
    public async Task<ActionResult<object>> Get(Guid appId, Guid entityId, CancellationToken ct)
    {
        var entity = await _db.StudioEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == TenantId && e.AppId == appId && e.EntityId == entityId, ct);
        if (entity is null) return NotFound();

        var attrs = await _db.StudioEntityAttributes.AsNoTracking()
            .Where(a => a.TenantId == TenantId && a.AppId == appId && a.EntityId == entityId)
            .OrderBy(a => a.Ordinal)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);

        return Ok(new
        {
            entity = new { entity.EntityId, entity.Name, entity.TableName },
            attributes = attrs.Select(a => new { a.AttributeId, a.Name, a.Type, a.Length, a.Required, a.Ordinal })
        });
    }

    public sealed record UpsertAttributeRequest(string Name, string Type, int? Length, bool Required, int Ordinal);

    [HttpPost("{entityId:guid}/attributes")]
    public async Task<ActionResult<object>> AddAttribute(Guid appId, Guid entityId, [FromBody] UpsertAttributeRequest req, CancellationToken ct)
    {
        var entity = await _db.StudioEntities
            .FirstOrDefaultAsync(e => e.TenantId == TenantId && e.AppId == appId && e.EntityId == entityId, ct);
        if (entity is null) return NotFound("Entity not found");

        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length < 1) return BadRequest("Attribute name required");

        var attr = new StudioEntityAttribute
        {
            TenantId = TenantId,
            AppId = appId,
            EntityId = entityId,
            Name = name,
            Type = NormalizeType(req.Type),
            Length = req.Length,
            Required = req.Required,
            Ordinal = req.Ordinal
        };
        _db.StudioEntityAttributes.Add(attr);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { attr.AttributeId });
    }

    private static string NormalizeType(string? t)
    {
        t = (t ?? "string").Trim().ToLowerInvariant();
        return t switch
        {
            "string" or "text" => "string",
            "int" or "int32" => "int",
            "bool" or "boolean" => "bool",
            "datetime" or "date" => "datetime",
            "decimal" or "money" => "decimal",
            _ => "string"
        };
    }

    private static string MakeSafe(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "Entity" : new string(chars);
    }
}
