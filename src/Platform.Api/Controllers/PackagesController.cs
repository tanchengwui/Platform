using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Storage;

namespace Platform.Api.Controllers;

[ApiController]
[Route("packages")]
public sealed class PackagesController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IArtifactStorage _storage;

    public PackagesController(PlatformDbContext db, IArtifactStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    [HttpGet("{packageId:guid}/download")]
    public async Task<IActionResult> Download(Guid packageId, CancellationToken ct)
    {
        var pkg = await _db.Packages
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PackageId == packageId, ct);

        if (pkg is null) return NotFound();

        if (string.IsNullOrWhiteSpace(pkg.StoragePath))
            return NotFound("Package storage key is empty.");

        if (!await _storage.ExistsAsync(pkg.StoragePath, ct))
            return NotFound("Package not found in artifact storage.");

        var stream = await _storage.OpenReadAsync(pkg.StoragePath, ct);
        return File(stream, _storage.GetContentType(pkg.StoragePath), fileDownloadName: $"{packageId}.zip");
    }
}
