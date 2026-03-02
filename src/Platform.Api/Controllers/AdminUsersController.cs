using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Security;

namespace Platform.Api.Controllers;

[ApiController]
[Route("admin")]
[Authorize(Policy = "USER_ADMIN")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public AdminUsersController(PlatformDbContext db)
    {
        _db = db;
    }

    private Guid GetTenantId()
    {
        var tid = User.FindFirst("tenant_id")?.Value;
        return Guid.TryParse(tid, out var g)
            ? g
            : throw new UnauthorizedAccessException("Missing tenant_id claim.");
    }

    public sealed record RoleDto(Guid RoleId, string Name);

    [HttpGet("roles")]
    public async Task<ActionResult<List<RoleDto>>> ListRoles()
    {
        var tenantId = GetTenantId();
        var roles = await _db.Roles
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(r.RoleId, r.Name))
            .ToListAsync();

        return roles;
    }

    public sealed record UserDto(Guid UserId, string Email, bool IsActive, DateTimeOffset CreatedAt, List<string> Roles);

    [HttpGet("users")]
    public async Task<ActionResult<List<UserDto>>> ListUsers()
    {
        var tenantId = GetTenantId();

        var users = await _db.Users
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Email)
            .Select(u => new { u.UserId, u.Email, u.IsActive, u.CreatedAt })
            .ToListAsync();

        var userIds = users.Select(u => u.UserId).ToList();

        var roleMap = await _db.UserRoles
            .Where(ur => userIds.Contains(ur.UserId))
            .Join(_db.Roles, ur => ur.RoleId, r => r.RoleId, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync();

        var dict = roleMap
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().OrderBy(x => x).ToList());

        var dtos = users
            .Select(u => new UserDto(
                u.UserId,
                u.Email,
                u.IsActive,
                u.CreatedAt,
                dict.TryGetValue(u.UserId, out var rs) ? rs : new List<string>()))
            .ToList();

        return dtos;
    }

    public sealed record CreateUserRequest(string Email, string Password, List<string> Roles);
    public sealed record CreateUserResponse(Guid UserId);

    [HttpPost("users")]
    public async Task<ActionResult<CreateUserResponse>> CreateUser([FromBody] CreateUserRequest req)
    {
        var tenantId = GetTenantId();

        var email = req.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest("Email is required.");

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
            return BadRequest("Password must be at least 6 characters.");

        var exists = await _db.Users.AnyAsync(u => u.TenantId == tenantId && u.Email == email);
        if (exists) return Conflict("Email already exists.");

        var roleNames = (req.Roles ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // default role if none specified
        if (roleNames.Count == 0) roleNames.Add("Viewer");

        var roles = await _db.Roles
            .Where(r => r.TenantId == tenantId && roleNames.Contains(r.Name))
            .ToListAsync();

        if (roles.Count != roleNames.Count)
            return BadRequest("One or more roles are invalid.");

        var user = new User
        {
            TenantId = tenantId,
            Email = email,
            PasswordHash = PasswordHasher.Hash(req.Password),
            IsActive = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        foreach (var r in roles)
            _db.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = r.RoleId });

        await _db.SaveChangesAsync();

        return new CreateUserResponse(user.UserId);
    }

    public sealed record SetRolesRequest(List<string> Roles);

    [HttpPut("users/{userId:guid}/roles")]
    public async Task<ActionResult> SetRoles(Guid userId, [FromBody] SetRolesRequest req)
    {
        var tenantId = GetTenantId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UserId == userId);
        if (user is null) return NotFound();

        var roleNames = (req.Roles ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roleNames.Count == 0) roleNames.Add("Viewer");

        var roles = await _db.Roles
            .Where(r => r.TenantId == tenantId && roleNames.Contains(r.Name))
            .ToListAsync();

        if (roles.Count != roleNames.Count)
            return BadRequest("One or more roles are invalid.");

        var existing = await _db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync();
        _db.UserRoles.RemoveRange(existing);
        foreach (var r in roles)
            _db.UserRoles.Add(new UserRole { UserId = userId, RoleId = r.RoleId });

        await _db.SaveChangesAsync();
        return Ok();
    }

    public sealed record ResetPasswordRequest(string NewPassword);

    [HttpPut("users/{userId:guid}/password")]
    public async Task<ActionResult> ResetPassword(Guid userId, [FromBody] ResetPasswordRequest req)
    {
        var tenantId = GetTenantId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UserId == userId);
        if (user is null) return NotFound();

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest("Password must be at least 6 characters.");

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        await _db.SaveChangesAsync();
        return Ok();
    }

    public sealed record SetActiveRequest(bool IsActive);

    [HttpPut("users/{userId:guid}/active")]
    public async Task<ActionResult> SetActive(Guid userId, [FromBody] SetActiveRequest req)
    {
        var tenantId = GetTenantId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UserId == userId);
        if (user is null) return NotFound();

        user.IsActive = req.IsActive;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
