using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Services;
using Platform.Api.Storage;
using System.Security.Claims;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize]
public sealed class PublishController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IArtifactStorage _storage;

    public PublishController(PlatformDbContext db, IArtifactStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    [Authorize(Policy = "APP_PUBLISH")]


    [HttpPost("publish")]
    public async Task<ActionResult<object>> Publish([FromBody] PublishRequest req, CancellationToken ct)
    {
        var draft = await _db.AppDrafts.FirstOrDefaultAsync(d => d.DraftId == req.DraftId, ct);
        if (draft is null) return NotFound("Draft not found.");

        var app = await _db.Apps.FirstOrDefaultAsync(a => a.AppId == draft.AppId && a.TenantId == TenantId, ct);
        if (app is null) return Forbid();

        var builder = new PackageBuilder(_db, _storage);

        var result = await builder.BuildFromDraftAsync(
            tenantId: TenantId,
            userId: UserId,
            draftId: req.DraftId,
            semVer: req.SemVer,
            ct: ct);

        // ServiceCenter expects these exact fields:
        return Ok(new
        {
            appVersionId = result.AppVersionId,
            semVer = result.SemVer,
            buildNo = result.BuildNo
        });
    }

    public sealed record PublishRequest(Guid DraftId, string SemVer);
}