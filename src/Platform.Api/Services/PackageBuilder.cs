using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Storage;

namespace Platform.Api.Services;

public sealed class PackageBuilder
{
    private readonly PlatformDbContext _db;
    private readonly IArtifactStorage _storage;

    public PackageBuilder(PlatformDbContext db, IArtifactStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public sealed record BuildResult(Guid AppVersionId, string SemVer, int BuildNo, Guid PackageId);

    public async Task<BuildResult> BuildFromDraftAsync(
        Guid tenantId,
        Guid userId,
        Guid draftId,
        string semVer,
        CancellationToken ct)
    {
        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == draftId, ct)
                    ?? throw new InvalidOperationException("Draft not found.");

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == tenantId, ct)
                  ?? throw new InvalidOperationException("App not found or tenant mismatch.");

        var resources = await _db.DraftResources
            .Where(r => r.DraftId == draftId)
            .Select(r => new { r.Type, r.Key, r.JsonPayload })
            .ToListAsync(ct);

        if (!resources.Any(r => r.Type == "UI_PAGE"))
            throw new InvalidOperationException("Draft must contain at least one UI_PAGE resource.");

        // entry page key: prefer Home else first UI_PAGE
        var entryPageKey = resources
            .Where(r => r.Type == "UI_PAGE")
            .Select(r => r.Key)
            .FirstOrDefault(k => string.Equals(k, "Home", StringComparison.OrdinalIgnoreCase))
            ?? resources.Where(r => r.Type == "UI_PAGE").Select(r => r.Key).First();

        // build number per app
        var lastBuild = await _db.AppVersions
            .Where(v => v.AppId == app.AppId)
            .OrderByDescending(v => v.BuildNo)
            .Select(v => (int?)v.BuildNo)
            .FirstOrDefaultAsync(ct);

        var buildNo = (lastBuild ?? 0) + 1;

        var appVersionId = Guid.NewGuid();
        var version = new AppVersion
        {
            AppVersionId = appVersionId,
            AppId = app.AppId,
            SemVer = semVer,
            BuildNo = buildNo,
            Status = VersionStatus.Published,
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            SourceDraftId = draftId
        };
        _db.AppVersions.Add(version);

        var packageId = Guid.NewGuid();

        var manifestObj = new
        {
            packageId,
            tenantId,
            appId = app.AppId,
            appKey = app.Key,
            appName = app.Name,
            appVersionId,
            semVer,
            buildNo,
            createdAt = version.CreatedAt,
            entryPageKey
        };

        var manifestJson = JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // provider-agnostic key
        var storageKey = $"packages/{tenantId:N}/{app.AppId:N}/{appVersionId:N}.zip";

        // build zip in memory
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // manifest
                var manifestEntry = archive.CreateEntry("app.manifest.json", CompressionLevel.Optimal);
                await using (var entryStream = manifestEntry.Open())
                await using (var writer = new StreamWriter(entryStream))
                {
                    await writer.WriteAsync(manifestJson);
                }

                // resources
                foreach (var r in resources)
                {
                    var safeType = MakeSafeName(r.Type);
                    var safeKey = MakeSafeName(r.Key);
                    var entryName = $"{safeType}/{safeKey}.json";

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var es = entry.Open();
                    await using var sw = new StreamWriter(es);
                    await sw.WriteAsync(r.JsonPayload);
                }
            }

            zipBytes = ms.ToArray();
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

        await using (var contentStream = new MemoryStream(zipBytes))
        {
            await _storage.WriteAsync(storageKey, contentStream, ct);
        }

        var pkg = new Package
        {
            PackageId = packageId,
            AppVersionId = appVersionId,
            StoragePath = storageKey,
            Sha256 = sha256,
            SizeBytes = zipBytes.LongLength,
            ManifestJson = manifestJson,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Packages.Add(pkg);

        // update draft
        draft.Status = DraftStatus.Published;
        draft.UpdatedAt = DateTimeOffset.UtcNow;

        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "APP_PUBLISHED",
            EntityType = "AppVersion",
            EntityId = appVersionId.ToString(),
            Summary = $"Published {app.Key} {semVer} (build {buildNo})",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        return new BuildResult(appVersionId, semVer, buildNo, packageId);
    }

    private static string MakeSafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "X";
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Replace('/', '_').Replace('\\', '_').Trim();
    }
}