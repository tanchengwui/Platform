using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Services;

/// <summary>
/// Creates (if missing) app-specific tables for ENTITY resources on publish.
/// Tables live in the same SQL Server database as PlatformDb (simple, fast dev mode).
/// Naming: AppData_<AppIdN>_<EntityName>
/// </summary>
public sealed class AppDataProvisioner
{
    private readonly PlatformDbContext _db;

    public AppDataProvisioner(PlatformDbContext db) => _db = db;

    public sealed record EntityField(string Name, string Type = "string", bool Required = false, int? Length = null);
    public sealed record EntityDef(string Name, List<EntityField> Fields);

    public async Task ProvisionFromDraftAsync(Guid tenantId, Guid draftId, CancellationToken ct)
    {
        var draft = await _db.AppDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.DraftId == draftId, ct)
                    ?? throw new InvalidOperationException("Draft not found.");

        // Read entity definitions
        var entities = await _db.DraftResources
            .AsNoTracking()
            .Where(r => r.DraftId == draftId && r.Type == "ENTITY")
            .Select(r => r.JsonPayload)
            .ToListAsync(ct);

        if (entities.Count == 0) return;

        foreach (var json in entities)
        {
            var def = JsonSerializer.Deserialize<EntityDef>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (def is null) continue;

            await EnsureTableAsync(draft.AppId, def, ct);
        }
    }

    private async Task EnsureTableAsync(Guid appId, EntityDef def, CancellationToken ct)
    {
        var table = MakeTableName(appId, def.Name);

        // Build columns SQL
        // Always have: Id uniqueidentifier PK
        var colDefs = new List<string>
        {
            "[Id] uniqueidentifier NOT NULL CONSTRAINT [PK_" + table + "] PRIMARY KEY"
        };

        foreach (var f in def.Fields)
        {
            var colName = SafeSqlIdent(f.Name);
            var sqlType = MapType(f);
            var nullSql = f.Required ? "NOT NULL" : "NULL";
            colDefs.Add($"{colName} {sqlType} {nullSql}");
        }

        var colsSql = string.Join(",\n    ", colDefs);

        // Create if not exists (no alter in v1)
        var sql = $@"
IF OBJECT_ID(N'[dbo].[{table}]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[{table}] (
    {colsSql}
    );
END";

        await _db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private static string MapType(EntityField f)
    {
        var t = (f.Type ?? "string").Trim().ToLowerInvariant();
        return t switch
        {
            "int" => "int",
            "decimal" => "decimal(18,2)",
            "bool" or "boolean" => "bit",
            "datetime" => "datetime2",
            _ => $"nvarchar({(f.Length is > 0 and <= 4000 ? f.Length : 200)})"
        };
    }

    private static string MakeTableName(Guid appId, string entityName)
    {
        var clean = RegexReplace(entityName);
        return $"AppData_{appId:N}_{clean}";
    }

    private static string RegexReplace(string s)
    {
        s ??= "Entity";
        s = s.Trim();
        if (s.Length == 0) s = "Entity";
        // keep A-Z a-z 0-9 _
        var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var v = new string(chars);
        while (v.Contains("__")) v = v.Replace("__", "_");
        return v.Trim('_');
    }

    private static string SafeSqlIdent(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "[X]";
        var chars = name.Trim().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var v = new string(chars);
        if (char.IsDigit(v.FirstOrDefault())) v = "_" + v;
        return "[" + v.Replace("]", "_") + "]";
    }
}
