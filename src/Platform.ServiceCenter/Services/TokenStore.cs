using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Platform.ServiceCenter.Services;

/// <summary>
/// Stores API tokens for Blazor Server.
/// Uses ProtectedLocalStorage (JS interop) -> must be called after first render.
/// </summary>
public sealed class TokenStore
{
    private const string Key = "platform.sc.tokens";
    private readonly ProtectedLocalStorage _storage;

    public TokenStore(ProtectedLocalStorage storage) => _storage = storage;

    public event Action? Changed;

    public bool IsInitialized { get; private set; }

    public string? Token { get; private set; }
    public string? RefreshToken { get; private set; }
    public Guid? TenantId { get; private set; }
    public string? Email { get; private set; }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(Token) && TenantId is not null;

    public async Task InitializeAsync()
    {
        var result = await _storage.GetAsync<State>(Key);
        if (result.Success && result.Value is not null)
        {
            Token = result.Value.Token;
            RefreshToken = result.Value.RefreshToken;
            TenantId = result.Value.TenantId;
            Email = result.Value.Email;
        }

        IsInitialized = true;
        Changed?.Invoke();
    }

    public async Task SetAsync(string token, string refreshToken, Guid tenantId, string email)
    {
        Token = token;
        RefreshToken = refreshToken;
        TenantId = tenantId;
        Email = email;

        await _storage.SetAsync(Key, new State(token, refreshToken, tenantId, email));

        IsInitialized = true;
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        Token = null;
        RefreshToken = null;
        TenantId = null;
        Email = null;

        await _storage.DeleteAsync(Key);

        IsInitialized = true;
        Changed?.Invoke();
    }

    private sealed record State(string Token, string RefreshToken, Guid TenantId, string Email);
}