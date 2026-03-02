using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Controllers;

[ApiController]
[Route("runtime")]
public sealed class RuntimeResolveController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public RuntimeResolveController(PlatformDbContext db) => _db = db;

    public sealed record ResolveActiveRequest(Guid EnvId, Guid AppId);

    public sealed record ResolveActiveResponse(
        Guid AppId,
        Guid EnvId,
        Guid ActiveAppVersionId,
        Guid PackageId,
        string Sha256,
        string DownloadUrl
    );

    [HttpPost("resolve-active")]
    public async Task<ActionResult<ResolveActiveResponse>> ResolveActive([FromBody] ResolveActiveRequest req, CancellationToken ct)
    {
        var envApp = await _db.EnvironmentApps
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EnvId == req.EnvId && x.AppId == req.AppId, ct);

        if (envApp is null || envApp.ActiveAppVersionId is null)
            return NotFound("No active version for this app/environment.");

        var package = await _db.Packages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.AppVersionId == envApp.ActiveAppVersionId.Value, ct);

        if (package is null)
            return NotFound("Active version has no package.");

        var downloadUrl = Url.ActionLink(
            action: nameof(PackagesController.Download),
            controller: "Packages",
            values: new { packageId = package.PackageId });

        return Ok(new ResolveActiveResponse(
            AppId: req.AppId,
            EnvId: req.EnvId,
            ActiveAppVersionId: envApp.ActiveAppVersionId.Value,
            PackageId: package.PackageId,
            Sha256: package.Sha256,
            DownloadUrl: downloadUrl!
        ));
    }
}
