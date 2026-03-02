using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Platform.Api.Controllers;

[ApiController]
[Route("apps/{appId:guid}/data/{entityName}")]
public sealed class DataController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IConfiguration _cfg;

    public DataController(PlatformDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    private bool IsAuthedOrInternal(out Guid tenantId)
    {
        tenantId = Guid.Empty;

        // JWT auth (if provided)
        if (User?.Identity?.IsAuthenticated == true)
        {
            var tid = User.FindFirstValue("tenant_id");
            if (!string.IsNullOrWhiteSpace(tid) && Guid.TryParse(tid, out tenantId))
                return true;
        }

        // Internal key (Runtime proxy)
        var key = _cfg.GetValue<string>("InternalApiKey");
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (Request.Headers.TryGetValue("X-Platform-Internal-Key", out var got) && got == key)
            {
                return true; // tenant will be resolved from appId
            }
        }

        return false;
    }

    [HttpGet]
    public async Task<ActionResult<object>> List(Guid appId, string entityName, CancellationToken ct)
    {
        if (!IsAuthedOrInternal(out var tenantId))
            return Unauthorized();

        var (app, entity, attrs) = await ResolveEntityAsync(appId, entityName, tenantId, ct);
        if (entity is null) return NotFound("Entity not found.");

        var cols = attrs.Where(a => a.Name != "Id").Select(a => a.Name).ToList();
        // Always include Id
        var selectCols = new List<string> { "[Id]" };
        selectCols.AddRange(cols.Select(c => $"[{c}]"));

        var sql = $"SELECT TOP (200) {string.Join(", ", selectCols)} FROM dbo.[{entity.TableName}] ORDER BY [CreatedAt] DESC";

        var rows = await QueryAsync(sql, Array.Empty<DbParameter>(), ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<object>> GetById(Guid appId, string entityName, Guid id, CancellationToken ct)
    {
        if (!IsAuthedOrInternal(out var tenantId))
            return Unauthorized();

        var (app, entity, attrs) = await ResolveEntityAsync(appId, entityName, tenantId, ct);
        if (entity is null) return NotFound("Entity not found.");

        var cols = attrs.Where(a => a.Name != "Id").Select(a => a.Name).ToList();
        var selectCols = new List<string> { "[Id]" };
        selectCols.AddRange(cols.Select(c => $"[{c}]"));

        var p = _db.Database.GetDbConnection().CreateCommand().CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;

        var sql = $"SELECT TOP (1) {string.Join(", ", selectCols)} FROM dbo.[{entity.TableName}] WHERE [Id] = @id";
        var rows = await QueryAsync(sql, new[] { p }, ct);
        var row = rows.FirstOrDefault();
        if (row is null) return NotFound();
        return Ok(row);
    }

    [HttpPost]
    public async Task<ActionResult<object>> Create(Guid appId, string entityName, [FromBody] JsonElement payload, CancellationToken ct)
    {
        if (!IsAuthedOrInternal(out var tenantId))
            return Unauthorized();

        var (app, entity, attrs) = await ResolveEntityAsync(appId, entityName, tenantId, ct);
        if (entity is null) return NotFound("Entity not found.");

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var dict = ParsePayload(payload);

        var insertCols = new List<string> { "[Id]" };
        var insertVals = new List<string> { "@Id" };
        var parameters = new List<DbParameter>();

        parameters.Add(MkParam("@Id", id));
        parameters.Add(MkParam("@CreatedAt", now));
        parameters.Add(MkParam("@UpdatedAt", now));

        insertCols.Add("[CreatedAt]"); insertVals.Add("@CreatedAt");
        insertCols.Add("[UpdatedAt]"); insertVals.Add("@UpdatedAt");

        foreach (var a in attrs)
        {
            if (a.Name is "Id" or "CreatedAt" or "UpdatedAt") continue;
            if (!dict.TryGetValue(a.Name, out var val)) continue;

            var pn = "@p_" + a.Name;
            insertCols.Add($"[{a.Name}]");
            insertVals.Add(pn);
            parameters.Add(MkParam(pn, Coerce(val, a)));
        }

        var sql = $"INSERT INTO dbo.[{entity.TableName}] ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", insertVals)})";

        await ExecAsync(sql, parameters.ToArray(), ct);

        // return created
        return await GetById(appId, entityName, id, ct);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult> Update(Guid appId, string entityName, Guid id, [FromBody] JsonElement payload, CancellationToken ct)
    {
        if (!IsAuthedOrInternal(out var tenantId))
            return Unauthorized();

        var (app, entity, attrs) = await ResolveEntityAsync(appId, entityName, tenantId, ct);
        if (entity is null) return NotFound("Entity not found.");

        var dict = ParsePayload(payload);

        var sets = new List<string>();
        var parameters = new List<DbParameter>
        {
            MkParam("@Id", id),
            MkParam("@UpdatedAt", DateTimeOffset.UtcNow)
        };
        sets.Add("[UpdatedAt] = @UpdatedAt");

        foreach (var a in attrs)
        {
            if (a.Name is "Id" or "CreatedAt" or "UpdatedAt") continue;
            if (!dict.TryGetValue(a.Name, out var val)) continue;

            var pn = "@p_" + a.Name;
            sets.Add($"[{a.Name}] = {pn}");
            parameters.Add(MkParam(pn, Coerce(val, a)));
        }

        if (sets.Count == 1) return NoContent(); // only UpdatedAt

        var sql = $"UPDATE dbo.[{entity.TableName}] SET {string.Join(", ", sets)} WHERE [Id] = @Id";
        await ExecAsync(sql, parameters.ToArray(), ct);

        return NoContent();
    }

    private async Task<(App? app, StudioEntity? entity, List<StudioEntityAttribute> attrs)> ResolveEntityAsync(Guid appId, string entityName, Guid tenantFromJwt, CancellationToken ct)
    {
        // resolve app + tenant
        var app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.AppId == appId, ct);
        if (app is null) return (null, null, new());

        // if JWT provided, enforce tenant match
        if (tenantFromJwt != Guid.Empty && app.TenantId != tenantFromJwt)
            return (app, null, new());

        // sanitize entityName
        entityName = (entityName ?? "").Trim();
        if (entityName.Length > 128) return (app, null, new());

        var entity = await _db.StudioEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == app.TenantId && e.AppId == appId &&
                                      (e.Name == entityName || e.TableName == entityName), ct);

        if (entity is null) return (app, null, new());

        var attrs = await _db.StudioEntityAttributes.AsNoTracking()
            .Where(a => a.EntityId == entity.EntityId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        return (app, entity, attrs);
    }

    private static Dictionary<string, JsonElement> ParsePayload(JsonElement payload)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (payload.ValueKind != JsonValueKind.Object) return dict;

        foreach (var p in payload.EnumerateObject())
            dict[p.Name] = p.Value;

        return dict;
    }

    private static object? Coerce(JsonElement v, StudioEntityAttribute a)
    {
        if (v.ValueKind == JsonValueKind.Null) return null;

        return a.Type switch
        {
            "Guid" => v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null,
            "Int32" => v.TryGetInt32(out var i) ? i : null,
            "Boolean" => v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : (bool?)null,
            "DateTimeOffset" => v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dto) ? dto : null,
            "Decimal" => v.TryGetDecimal(out var d) ? d : null,
            _ => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        };
    }

    private DbParameter MkParam(string name, object? value)
    {
        var p = _db.Database.GetDbConnection().CreateCommand().CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        return p;
    }

    private async Task<int> ExecAsync(string sql, DbParameter[] parameters, CancellationToken ct)
    {
        await using var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters) cmd.Parameters.Add(p);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, DbParameter[] parameters, CancellationToken ct)
    {
        await using var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters) cmd.Parameters.Add(p);

        var rows = new List<Dictionary<string, object?>>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var val = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                row[name] = val;
            }
            rows.Add(row);
        }

        return rows;
    }
}
