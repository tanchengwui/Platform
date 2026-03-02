using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Security;
using System.Security.Claims;

namespace Platform.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly RefreshTokenService _refresh;

    public AuthController(PlatformDbContext db, JwtTokenService jwt, RefreshTokenService refresh)
    {
        _db = db;
        _jwt = jwt;
        _refresh = refresh;
    }

    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string Token, string RefreshToken, Guid UserId, Guid TenantId, string Email);

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email && u.IsActive);
        if (user is null) return Unauthorized("Invalid credentials.");

        if (!PasswordHasher.Verify(req.Password, user.PasswordHash))
            return Unauthorized("Invalid credentials.");

        // roles are optional for now; return empty list if not found
        var roleIds = await _db.UserRoles.Where(x => x.UserId == user.UserId).Select(x => x.RoleId).ToListAsync();
        var roleNames = await _db.Roles.Where(r => roleIds.Contains(r.RoleId)).Select(r => r.Name).ToListAsync();

        var permIds = await _db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionId)
            .Distinct()
            .ToListAsync();

        var permissions = await _db.Permissions
            .Where(p => permIds.Contains(p.PermissionId))
            .Select(p => p.Code)
            .ToListAsync();

        var token = _jwt.CreateToken(user.UserId, user.TenantId, user.Email, roleNames, permissions);
        var device = Request.Headers.UserAgent.ToString();
        var issued = await _refresh.IssueAsync(user.UserId, user.TenantId, device, HttpContext.RequestAborted);
        return new LoginResponse(token, issued.RefreshToken, user.UserId, user.TenantId, user.Email);
    }


public sealed record RefreshRequest(Guid TenantId, string RefreshToken);
public sealed record RefreshResponse(string Token, string RefreshToken);

[HttpPost("refresh")]
public async Task<ActionResult<RefreshResponse>> Refresh([FromBody] RefreshRequest req)
{
    var (ok, tokenEntity) = await _refresh.ValidateAsync(req.TenantId, req.RefreshToken, HttpContext.RequestAborted);
    if (!ok || tokenEntity is null) return Unauthorized("Invalid refresh token.");

    var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == tokenEntity.UserId && u.IsActive);
    if (user is null) return Unauthorized("User inactive.");

    var roleIds = await _db.UserRoles.Where(x => x.UserId == user.UserId).Select(x => x.RoleId).ToListAsync();
    var roleNames = await _db.Roles.Where(r => roleIds.Contains(r.RoleId)).Select(r => r.Name).ToListAsync();

        var permIds = await _db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionId)
            .Distinct()
            .ToListAsync();

        var permissions = await _db.Permissions
            .Where(p => permIds.Contains(p.PermissionId))
            .Select(p => p.Code)
            .ToListAsync();

    var jwt = _jwt.CreateToken(user.UserId, user.TenantId, user.Email, roleNames, permissions);
    var device = Request.Headers.UserAgent.ToString();
    var rotated = await _refresh.RotateAsync(tokenEntity, device, HttpContext.RequestAborted);

    return new RefreshResponse(jwt, rotated.RefreshToken);
}

public sealed record LogoutRequest(Guid TenantId, string RefreshToken);

[HttpPost("logout")]
public async Task<ActionResult> Logout([FromBody] LogoutRequest req)
{
    await _refresh.RevokeAsync(req.TenantId, req.RefreshToken, HttpContext.RequestAborted);
    return Ok();
}
}
