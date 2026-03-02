using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.Json;

namespace Platform.ServiceCenter;

public sealed class SimpleAuthStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _currentUser = new(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(_currentUser));

    public void SignInFromJwt(string jwtToken, string? emailFallback = null)
    {
        if (string.IsNullOrWhiteSpace(jwtToken))
        {
            SignOut();
            return;
        }

        var claims = JwtClaimParser.Parse(jwtToken).ToList();

        // Ensure we have a Name claim for UI display
        var email = claims.FirstOrDefault(c => c.Type is "email" or ClaimTypes.Email)?.Value
                    ?? emailFallback
                    ?? "user";

        if (!claims.Any(c => c.Type == ClaimTypes.Name))
            claims.Add(new Claim(ClaimTypes.Name, email));

        var identity = new ClaimsIdentity(claims, authenticationType: "Jwt");
        _currentUser = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public void SignOut()
    {
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public void Notify()
        => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}

/// <summary>
/// Minimal JWT payload decoder for Blazor Server UI.
/// We trust signature validation happens on API; UI only uses claims for show/hide.
/// </summary>
internal static class JwtClaimParser
{
    public static IEnumerable<Claim> Parse(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return Array.Empty<Claim>();

        var json = DecodeBase64Url(parts[1]);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<Claim>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var claims = new List<Claim>();

        foreach (var prop in root.EnumerateObject())
        {
            // roles can be string or array, depending on token writer
            if (prop.Name is "role" or "roles")
            {
                AddStringOrArray(claims, ClaimTypes.Role, prop.Value);
                continue;
            }

            // our permission claim type
            if (prop.Name is "perm" or "permission" or "permissions")
            {
                AddStringOrArray(claims, "perm", prop.Value);
                continue;
            }

            // normalize common fields
            if (prop.Name == "email")
            {
                claims.Add(new Claim("email", prop.Value.GetString() ?? ""));
                claims.Add(new Claim(ClaimTypes.Email, prop.Value.GetString() ?? ""));
                continue;
            }

            if (prop.Name == "sub")
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, prop.Value.GetString() ?? ""));
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.String)
                claims.Add(new Claim(prop.Name, prop.Value.GetString() ?? ""));
        }

        return claims;
    }

    private static void AddStringOrArray(List<Claim> claims, string type, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            claims.Add(new Claim(type, value.GetString() ?? ""));
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in value.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String)
                    claims.Add(new Claim(type, v.GetString() ?? ""));
            }
        }
    }

    private static string DecodeBase64Url(string input)
    {
        input = input.Replace('-', '+').Replace('_', '/');
        switch (input.Length % 4)
        {
            case 2: input += "=="; break;
            case 3: input += "="; break;
        }

        try
        {
            var bytes = Convert.FromBase64String(input);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
