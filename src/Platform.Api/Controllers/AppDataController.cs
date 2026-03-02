using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Security;

namespace Platform.Api.Controllers;

[ApiController]
[Route("data/apps/{envId:guid}/{appId:guid}/entities/{entity}")]
[InternalKey]
public sealed class AppDataController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public AppDataController(PlatformDbContext db) => _db = db;

    // NOTE: envId is currently only used for routing symmetry.
    // In v1, data is per-app (not per-env). You can extend later.

    [HttpGet("rows")]
    public async Task<ActionResult<List<Dictionary<string, object?>>>> ListRows(Guid envId, Guid appId, string entity, CancellationToken ct)
    {
        var table = MakeTableName(appId, entity);
        var sql = $"SELECT TOP (200) * FROM [dbo].[{table}] ORDER BY [Id] DESC";

        await using var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var val = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                d[ToCamel(name)] = val;
            }
            rows.Add(d);
        }

        return rows;
    }

    [HttpGet("rows/{id:guid}")]
    public async Task<ActionResult<Dictionary<string, object?>>> GetRow(Guid envId, Guid appId, string entity, Guid id, CancellationToken ct)
    {
        var table = MakeTableName(appId, entity);
        var sql = $"SELECT * FROM [dbo].[{table}] WHERE [Id] = @id";

        await using var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return NotFound();

        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var val = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            d[ToCamel(name)] = val;
        }
        return d;
    }

    [HttpPost("rows")]
    public async Task<ActionResult<object>> CreateRow(Guid envId, Guid appId, string entity, [FromBody] JsonElement payload, CancellationToken ct)
    {
        var table = MakeTableName(appId, entity);
        var id = Guid.NewGuid();

        var dict = ToStringObjectDict(payload);

        // Build insert
        var cols = new List<string> { "[Id]" };
        var vals = new List<string> { "@id" };
        var parms = new List<(string name, object? value)> { ("@id", id) };

        var iParam = 0;
        foreach (var kv in dict)
        {
            if (!IsSafeIdent(kv.Key)) continue;
            var pName = "@p" + (iParam++);
            cols.Add("[" + kv.Key + "]");
            vals.Add(pName);
            parms.Add((pName, kv.Value));
        }

        var sql = $"INSERT INTO [dbo].[{table}] ({string.Join(",", cols)}) VALUES ({string.Join(",", vals)})";

        await using var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var pp in parms)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = pp.name;
            p.Value = pp.value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        await cmd.ExecuteNonQueryAsync(ct);
        return Ok(new { id });
    }

    [HttpPut("rows/{id:guid}")]
    public async Task<IActionResult> UpdateRow(Guid envId, Guid appId, string entity, Guid id, [FromBody] JsonElement payload, CancellationToken ct)
    {
        var table = MakeTableName(appId, entity);
        var dict = ToStringObjectDict(payload);

        var sets = new List<string>();
        var parms = new List<(string name, object? value)>();
        var iParam = 0;

        foreach (var kv in dict)
        {
            if (!IsSafeIdent(kv.Key)) continue;
            var pName = "@p" + (iParam++);
            sets.Add("[" + kv.Key + "]=" + pName);
            parms.Add((pName, kv.Value));
        }

        if (sets.Count == 0) return BadRequest("No fields to update.");

        var sql = $"UPDATE [dbo].[{table}] SET {string.Join(",", sets)} WHERE [Id]=@id";

        await using var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var pid = cmd.CreateParameter();
        pid.ParameterName = "@id";
        pid.Value = id;
        cmd.Parameters.Add(pid);

        foreach (var pp in parms)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = pp.name;
            p.Value = pp.value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected == 0) return NotFound();
        return NoContent();
    }

    private static string MakeTableName(Guid appId, string entityName)
    {
        entityName ??= "Entity";
        entityName = entityName.Trim();
        if (entityName.Length == 0) entityName = "Entity";
        var clean = new string(entityName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        while (clean.Contains("__")) clean = clean.Replace("__", "_");
        clean = clean.Trim('_');
        return $"AppData_{appId:N}_{clean}";
    }

    private static bool IsSafeIdent(string name)
        => !string.IsNullOrWhiteSpace(name) && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static Dictionary<string, object?> ToStringObjectDict(JsonElement el)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind != JsonValueKind.Object) return d;

        foreach (var p in el.EnumerateObject())
        {
            // Keep original casing from designer; SQL uses sanitized anyway
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? l : p.Value.TryGetDecimal(out var dec) ? dec : (object?)p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => p.Value.ToString()
            };
        }

        return d;
    }

    private static string ToCamel(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length == 1) return s.ToLowerInvariant();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}
