using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Services;

/// <summary>
/// Very small v1: creates/updates a table for a StudioEntity.
/// Called at Publish time in later steps.
/// </summary>
public sealed class StudioProvisioner
{
    private readonly PlatformDbContext _db;
    private readonly IConfiguration _cfg;

    public StudioProvisioner(PlatformDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public async Task EnsureTableAsync(Guid tenantId, Guid appId, Guid entityId, CancellationToken ct)
    {
        var entity = await _db.StudioEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.AppId == appId && e.EntityId == entityId, ct);
        if (entity is null) return;

        var attrs = await _db.StudioEntityAttributes.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AppId == appId && a.EntityId == entityId)
            .OrderBy(a => a.Ordinal)
            .ToListAsync(ct);

        var cols = new List<string>
        {
            "[Id] uniqueidentifier NOT NULL PRIMARY KEY DEFAULT NEWID()"
        };

        foreach (var a in attrs)
        {
            var colName = Bracket(a.Name);
            var sqlType = a.Type switch
            {
                "int" => "int",
                "bool" => "bit",
                "datetime" => "datetime2",
                "decimal" => "decimal(18,2)",
                _ => $"nvarchar({(a.Length is > 0 ? a.Length : 200)})"
            };
            cols.Add($"{colName} {sqlType} {(a.Required ? "NOT NULL" : "NULL")}");
        }

        var createSql = $@"
IF OBJECT_ID(N'[dbo].[{entity.TableName}]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[{entity.TableName}] (
        {string.Join(",\n        ", cols)}
    );
END
";

        await using var conn = new SqlConnection(_cfg.GetConnectionString("DefaultConnection"));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(createSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Bracket(string name)
    {
        name = new string((name ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "Col";
        return $"[{name}]";
    }
}
