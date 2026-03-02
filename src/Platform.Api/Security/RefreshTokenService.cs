using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;

namespace Platform.Api.Security;

public sealed class RefreshTokenService
{
    private readonly PlatformDbContext _db;

    // Access token is still handled by JwtTokenService; refresh token rotates here.
    public RefreshTokenService(PlatformDbContext db) => _db = db;

    public sealed record IssueResult(string RefreshToken, RefreshToken Entity);

    public async Task<IssueResult> IssueAsync(Guid userId, Guid tenantId, string? device, CancellationToken ct)
    {
        var raw = GenerateTokenString();
        var hash = Sha256Hex(raw);

        var entity = new RefreshToken
        {
            UserId = userId,
            TenantId = tenantId,
            TokenHash = hash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
            CreatedAt = DateTimeOffset.UtcNow,
            Device = device
        };

        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new IssueResult(raw, entity);
    }

    public async Task<(bool ok, RefreshToken? token)> ValidateAsync(Guid tenantId, string refreshToken, CancellationToken ct)
    {
        var hash = Sha256Hex(refreshToken);

        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TokenHash == hash, ct);

        if (token is null) return (false, null);
        if (token.RevokedAt is not null) return (false, null);
        if (token.ExpiresAt <= DateTimeOffset.UtcNow) return (false, null);

        return (true, token);
    }

    public async Task<IssueResult> RotateAsync(RefreshToken current, string? device, CancellationToken ct)
    {
        // revoke current and issue new
        current.RevokedAt = DateTimeOffset.UtcNow;

        var issued = await IssueAsync(current.UserId, current.TenantId, device, ct);
        current.ReplacedByTokenId = issued.Entity.TokenId;

        await _db.SaveChangesAsync(ct);
        return issued;
    }

    public async Task RevokeAsync(Guid tenantId, string refreshToken, CancellationToken ct)
    {
        var hash = Sha256Hex(refreshToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TokenHash == hash, ct);
        if (token is null) return;

        if (token.RevokedAt is null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string GenerateTokenString()
    {
        // 32 bytes => 43 chars base64url roughly
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);

        var b64 = Convert.ToBase64String(bytes);
        return b64.Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
