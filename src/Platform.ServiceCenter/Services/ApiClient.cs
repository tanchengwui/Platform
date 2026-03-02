using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace Platform.ServiceCenter.Services;
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static async Task EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = "";
        try { body = await resp.Content.ReadAsStringAsync(); } catch { /* ignore */ }
        throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}");
    }
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenStore _tokens;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    public ApiClient(IHttpClientFactory httpFactory, TokenStore tokens)
    {
        _httpFactory = httpFactory;
        _tokens = tokens;
    }
    private HttpClient CreateAuthed()
    {
        var http = _httpFactory.CreateClient("api");
        if (!string.IsNullOrWhiteSpace(_tokens.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.Token);
        return http;
    }
    private HttpClient CreatePlain() => _httpFactory.CreateClient("api");
    private static StringContent JsonBody<T>(T obj)
        => new(JsonSerializer.Serialize(obj, JsonOpts), Encoding.UTF8, "application/json");
    private static async Task EnsureSuccessOrThrow(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync();
        var msg = $"HTTP {(int)res.StatusCode} ({res.ReasonPhrase})." +
                  (string.IsNullOrWhiteSpace(body) ? string.Empty : $" Body: {body}");
        throw new InvalidOperationException(msg);
    }
    // ✅ Send request, if 401 then refresh once and retry
    private async Task<HttpResponseMessage> SendWithRefreshAsync(Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        using var http = CreateAuthed();
        var res = await send(http);
        if (res.StatusCode != HttpStatusCode.Unauthorized)
            return res;
        // try refresh once
        if (_tokens.TenantId is null || string.IsNullOrWhiteSpace(_tokens.RefreshToken))
            return res;
        res.Dispose();
        await RefreshAsync();
        using var http2 = CreateAuthed();
        return await send(http2);
    }
    // ---------- AUTH ----------
    public async Task<LoginResponse> LoginAsync(string email, string password)
    {
        using var http = CreatePlain();
        var res = await http.PostAsync("/auth/login", JsonBody(new LoginRequest(email, password)));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<LoginResponse>(body, JsonOpts)
                   ?? throw new InvalidOperationException("Login response is empty or invalid JSON.");
        await _tokens.SetAsync(data.Token, data.RefreshToken, data.TenantId, data.Email);
        return data;
    }
    public async Task RefreshAsync()
    {
        if (_tokens.TenantId is null || string.IsNullOrWhiteSpace(_tokens.RefreshToken))
            throw new InvalidOperationException("No refresh token available.");
        using var http = CreatePlain();
        var res = await http.PostAsync("/auth/refresh",
            JsonBody(new RefreshRequest(_tokens.TenantId.Value, _tokens.RefreshToken!)));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<RefreshResponse>(body, JsonOpts)
                   ?? throw new InvalidOperationException("Refresh response is empty or invalid JSON.");
        await _tokens.SetAsync(data.Token, data.RefreshToken, _tokens.TenantId.Value, _tokens.Email ?? "");
    }
    public async Task LogoutAsync()
    {
        // best-effort revoke on server
        if (_tokens.TenantId is not null && !string.IsNullOrWhiteSpace(_tokens.RefreshToken))
        {
            using var http = CreateAuthed();
            var _ = await http.PostAsync("/auth/logout",
                JsonBody(new LogoutRequest(_tokens.TenantId.Value, _tokens.RefreshToken!)));
        }
        await _tokens.ClearAsync();
    }
    // ---------- APPS ----------
    public async Task<List<AppDto>> ListAppsAsync()
    {
        var res = await SendWithRefreshAsync(h => h.GetAsync("/apps"));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<AppDto>>(body, JsonOpts) ?? new();
    }
    public async Task<AppDto> CreateAppAsync(string key, string name)
    {
        var res = await SendWithRefreshAsync(h => h.PostAsync("/apps", JsonBody(new CreateAppRequest(key, name))));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AppDto>(body, JsonOpts)
               ?? throw new InvalidOperationException("CreateApp response is empty or invalid JSON.");
    }
    public async Task<AppDraftDto> CreateDraftAsync(Guid appId)
    {
        var res = await SendWithRefreshAsync(h => h.PostAsync($"/apps/{appId}/drafts", content: null));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AppDraftDto>(body, JsonOpts)
               ?? throw new InvalidOperationException("CreateDraft response is empty or invalid JSON.");
    }
    // ---------- DRAFT RESOURCES ----------
    public async Task SavePageAsync(Guid draftId, string pageKey, string title, string bodyText)
    {
        var url = $"/drafts/{draftId}/resources/UI_PAGE/{Uri.EscapeDataString(pageKey)}";
        var res = await SendWithRefreshAsync(h => h.PutAsync(url, JsonBody(new PagePayload(title, bodyText))));
        await EnsureSuccessOrThrow(res);
    }
    // ---------- PUBLISH ----------
    public async Task<PublishResponse> PublishAsync(Guid draftId, string semVer)
    {
        var res = await SendWithRefreshAsync(h => h.PostAsync("/publish", JsonBody(new PublishRequest(draftId, semVer))));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<PublishResponse>(body, JsonOpts)
               ?? throw new InvalidOperationException("Publish response is empty or invalid JSON.");
    }
    // ---------- ENV / DEPLOY ----------
    public async Task<List<EnvironmentDto>> ListEnvironmentsAsync()
    {
        var res = await SendWithRefreshAsync(h => h.GetAsync("/environments"));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<EnvironmentDto>>(body, JsonOpts) ?? new();
    }
    public async Task<DeployResponse> DeployAsync(Guid envId, Guid appVersionId)
    {
        var res = await SendWithRefreshAsync(h => h.PostAsync($"/environments/{envId}/deploy/{appVersionId}", content: null));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DeployResponse>(body, JsonOpts)
               ?? throw new InvalidOperationException("Deploy response is empty or invalid JSON.");
    }
    public async Task<List<DeploymentDto>> ListDeploymentsAsync(Guid envId, Guid appId)
    {
        var res = await SendWithRefreshAsync(h => h.GetAsync($"/environments/{envId}/apps/{appId}/deployments"));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<DeploymentDto>>(body, JsonOpts) ?? new();
    }
    
public async Task<List<DeploymentDto>> RecentDeploymentsAsync(int take = 50)
{
    var res = await SendWithRefreshAsync(h => h.GetAsync($"/environments/deployments/recent?take={take}"));
    await EnsureSuccessOrThrow(res);
    var body = await res.Content.ReadAsStringAsync();
    return JsonSerializer.Deserialize<List<DeploymentDto>>(body, JsonOpts) ?? new();
}
public sealed record DeploymentLogDto(DateTimeOffset Timestamp, string Level, string Message);
public async Task<List<DeploymentLogDto>> GetDeploymentLogsAsync(Guid deploymentId, int take = 200)
{
    var res = await SendWithRefreshAsync(h => h.GetAsync($"/environments/deployments/{deploymentId}/logs?take={take}"));
    await EnsureSuccessOrThrow(res);
    var body = await res.Content.ReadAsStringAsync();
    return JsonSerializer.Deserialize<List<DeploymentLogDto>>(body, JsonOpts) ?? new();
}
public async Task CancelDeploymentAsync(Guid deploymentId)
{
    var res = await SendWithRefreshAsync(h => h.PostAsync($"/environments/deployments/{deploymentId}/cancel", content: null));
    await EnsureSuccessOrThrow(res);
}
public async Task<DeployResponse> RetryDeploymentAsync(Guid deploymentId)
{
    var res = await SendWithRefreshAsync(h => h.PostAsync($"/environments/deployments/{deploymentId}/retry", content: null));
    await EnsureSuccessOrThrow(res);
    var body = await res.Content.ReadAsStringAsync();
    // Reuse DeployResponse shape (deploymentId/status)
    return JsonSerializer.Deserialize<DeployResponse>(body, JsonOpts)
           ?? throw new InvalidOperationException("Retry response is empty or invalid JSON.");
}
// ---------- ADMIN / USERS ----------
    public async Task<List<RoleDto>> ListRolesAsync()
    {
        var res = await SendWithRefreshAsync(h => h.GetAsync("/admin/roles"));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<RoleDto>>(body, JsonOpts) ?? new();
    }
    public async Task<List<UserAdminDto>> ListUsersAsync()
    {
        var res = await SendWithRefreshAsync(h => h.GetAsync("/admin/users"));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<UserAdminDto>>(body, JsonOpts) ?? new();
    }
    public async Task<Guid> CreateUserAsync(string email, string password, List<string> roles)
    {
        var res = await SendWithRefreshAsync(h => h.PostAsync("/admin/users", JsonBody(new CreateUserAdminRequest(email, password, roles))));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<CreateUserAdminResponse>(body, JsonOpts)
                   ?? throw new InvalidOperationException("CreateUser response is empty or invalid JSON.");
        return data.UserId;
    }
    public async Task SetUserRolesAsync(Guid userId, List<string> roles)
    {
        var res = await SendWithRefreshAsync(h => h.PutAsync($"/admin/users/{userId}/roles", JsonBody(new SetRolesAdminRequest(roles))));
        await EnsureSuccessOrThrow(res);
    }
    public async Task ResetUserPasswordAsync(Guid userId, string newPassword)
    {
        var res = await SendWithRefreshAsync(h => h.PutAsync($"/admin/users/{userId}/password", JsonBody(new ResetPasswordAdminRequest(newPassword))));
        await EnsureSuccessOrThrow(res);
    }
    public async Task SetUserActiveAsync(Guid userId, bool isActive)
    {
        var res = await SendWithRefreshAsync(h => h.PutAsync($"/admin/users/{userId}/active", JsonBody(new SetActiveAdminRequest(isActive))));
        await EnsureSuccessOrThrow(res);
    }
    // ---------- DTOs ----------
    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string Token, string RefreshToken, Guid UserId, Guid TenantId, string Email);
    public sealed record RefreshRequest(Guid TenantId, string RefreshToken);
    public sealed record RefreshResponse(string Token, string RefreshToken);
    public sealed record LogoutRequest(Guid TenantId, string RefreshToken);
    public sealed record CreateAppRequest(string Key, string Name);
    public sealed record AppDto(Guid AppId, Guid TenantId, string Key, string Name);
    public sealed record AppDraftDto(Guid DraftId, Guid AppId, int Version, string Status);
    public sealed record PagePayload(string title, string bodyText);
    public sealed record PublishRequest(Guid DraftId, string SemVer);
    public sealed record PublishResponse(Guid AppVersionId, string SemVer, int BuildNo); 
    public sealed record CreateEntityRequest(string Name);
    public sealed record AddAttributeRequest(string Name, string Type, int? Length, bool Required);

    public sealed class EnvironmentDto
    {
        public Guid EnvId { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = "";
        public int Type { get; set; }
        public string? BaseUrl { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
    public sealed class DeployResponse
    {
        public Guid DeploymentId { get; set; }
        public int Status { get; set; }          // ✅ API returns number
        public string? RuntimeUrlHint { get; set; }
        // optional UI helper
        public string StatusText => Status switch
{
    1 => "Queued",
    2 => "Running",
    3 => "Succeeded",
    4 => "Failed",
    6 => "Canceled",
    _ => Status.ToString()
};
    }
    public sealed class DeploymentDto
{
    public Guid DeploymentId { get; set; }
    public Guid EnvId { get; set; }
    public Guid AppId { get; set; }
    public Guid AppVersionId { get; set; }
    public string SemVer { get; set; } = "";
    public int BuildNo { get; set; }
    public int Attempt { get; set; }
    public bool CancelRequested { get; set; }
    public int Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? LogRef { get; set; }
    public string StatusText => Status switch
    {
        1 => "Queued",
        2 => "Running",
        3 => "Succeeded",
        4 => "Failed",
        6 => "Canceled",
        _ => Status.ToString()
    };
}
    public sealed record RoleDto(Guid RoleId, string Name);
    public sealed record UserAdminDto(Guid UserId, string Email, bool IsActive, DateTimeOffset CreatedAt, List<string> Roles);
    public sealed record CreateUserAdminRequest(string Email, string Password, List<string> Roles);
    public sealed record CreateUserAdminResponse(Guid UserId);
    public sealed record SetRolesAdminRequest(List<string> Roles);
    public sealed record ResetPasswordAdminRequest(string NewPassword);
    public sealed record SetActiveAdminRequest(bool IsActive);
    // ===== Entity Designer DTOs =====
    public sealed class EntityFieldDto
    {
        // Convenience ctor used by EntityDesigner UI (name/type/required)
        public EntityFieldDto(string name, string type, bool required)
        {
            AttributeId = Guid.NewGuid();
            EntityId = Guid.Empty;
            Name = name;
            Type = type;
            Length = null;
            Required = required;
        }
        public Guid AttributeId { get; set; }
        public Guid EntityId { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "string";
        public int? Length { get; set; }
        public bool Required { get; set; }
        // Full constructor (server shape)
        public EntityFieldDto(Guid attributeId, Guid entityId, string name, string type, int? length, bool required)
        {
            AttributeId = attributeId;
            EntityId = entityId;
            Name = name;
            Type = type;
            Length = length;
            Required = required;
        }
        // Convenience ctor used by UI (no length passed)
        public EntityFieldDto(Guid attributeId, Guid entityId, string name, string type, bool required = false)
        {
            AttributeId = attributeId;
            EntityId = entityId;
            Name = name;
            Type = type;
            Length = null;
            Required = required;
        }
        // Parameterless for JSON
        public EntityFieldDto() { }
    }
    public sealed class EntityDefDto
    {
        // Convenience ctor used by EntityDesigner UI (name/fields)
        public EntityDefDto(string name, List<EntityFieldDto> fields)
        {
            EntityId = Guid.NewGuid();
            TenantId = Guid.Empty;
            AppId = Guid.Empty;
            Name = name;
            TableName = "AppData_" + name;
            Fields = fields ?? new();
        }
        public Guid EntityId { get; set; }
        public Guid TenantId { get; set; }
        public Guid AppId { get; set; }
        public string Name { get; set; } = "";
        public string TableName { get; set; } = "";
        public List<EntityFieldDto> Fields { get; set; } = new();
        // Full constructor (server shape)
        public EntityDefDto(Guid entityId, Guid tenantId, Guid appId, string name, string tableName, List<EntityFieldDto> fields)
        {
            EntityId = entityId;
            TenantId = tenantId;
            AppId = appId;
            Name = name;
            TableName = tableName;
            Fields = fields ?? new();
        }
        // Convenience ctor used by UI (no appId passed)
        public EntityDefDto(Guid entityId, Guid tenantId, string name, string tableName, List<EntityFieldDto> fields)
        {
            EntityId = entityId;
            TenantId = tenantId;
            AppId = Guid.Empty; // API can infer / overwrite
            Name = name;
            TableName = tableName;
            Fields = fields ?? new();
        }
        // Parameterless for JSON
        public EntityDefDto() { }
    }

    // ===== Entity Designer API (DraftResources) =====
    public async Task<List<EntityDefDto>> ListDraftEntitiesAsync(Guid draftId, CancellationToken ct = default)
    {
        var res = await SendWithRefreshAsync(h => h.GetAsync($"/drafts/{draftId}/entities", ct));
        await EnsureSuccessOrThrow(res);
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<EntityDefDto>>(body, JsonOpts) ?? new();
    }

    public async Task UpsertDraftEntityAsync(Guid draftId, string entityName, EntityDefDto dto, CancellationToken ct = default)
    {
        var res = await SendWithRefreshAsync(h => h.PutAsync($"/drafts/{draftId}/entities/{Uri.EscapeDataString(entityName)}", JsonBody(dto), ct));
        await EnsureSuccessOrThrow(res);
    }

    public async Task GenerateCrudAsync(Guid draftId, string entityName, CancellationToken ct = default)
    {
        var res = await SendWithRefreshAsync(h => h.PostAsync($"/drafts/{draftId}/entities/{Uri.EscapeDataString(entityName)}/generate-crud", content: null, ct));
        await EnsureSuccessOrThrow(res);
    }
}
