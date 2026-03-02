using System.Net;
using System.Net.Http.Json;

// ====== CONFIG ======
var apiBase = "http://localhost:5002";

// Put a REAL envId from your Environments table:
var envIdText = "853F8D82-C14D-460F-99B4-0D4B3240E2AD";

// ✅ from your user table
var emailText = "admin@local";

// This must be the PLAINTEXT password used when seeding/creating the user.
// Not the hash string in DB.
var passwordText = "Admin123!";
// ====================

if (!Guid.TryParse(envIdText, out var envId))
{
    Console.WriteLine($"Invalid envId: {envIdText}");
    return;
}

var cookies = new CookieContainer();
using var http = new HttpClient(new HttpClientHandler
{
    UseCookies = true,
    CookieContainer = cookies
})
{
    BaseAddress = new Uri(apiBase)
};

Console.WriteLine("1) Login...");
var loginResp = await http.PostAsJsonAsync("/auth/login", new LoginRequest
{
    Email = emailText,
    Password = passwordText
});

Console.WriteLine($"   Status: {(int)loginResp.StatusCode} {loginResp.StatusCode}");
if (!loginResp.IsSuccessStatusCode)
{
    Console.WriteLine(await loginResp.Content.ReadAsStringAsync());
    return;
}

Console.WriteLine("2) GET /apps ...");
var apps = await http.GetFromJsonAsync<List<AppDto>>("/apps") ?? new();
Console.WriteLine($"   Apps: {apps.Count}");

var firstDeployable = apps.FirstOrDefault(a => a.LatestPublishedVersionId != null);
if (firstDeployable is null)
{
    Console.WriteLine("   No deployable app found. Publish an app first.");
    return;
}

Console.WriteLine($"3) Deploy '{firstDeployable.Name}' latest published version...");
Console.WriteLine($"   envId={envId}");
Console.WriteLine($"   appVersionId={firstDeployable.LatestPublishedVersionId}");

var deployUrl = $"/environments/{envId}/deploy/{firstDeployable.LatestPublishedVersionId}";
var deployResp = await http.PostAsync(deployUrl, content: null);

Console.WriteLine($"   Status: {(int)deployResp.StatusCode} {deployResp.StatusCode}");
var deployBody = await deployResp.Content.ReadAsStringAsync();
if (!string.IsNullOrWhiteSpace(deployBody))
    Console.WriteLine(deployBody);

// ---------- DTOs ----------
public sealed class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AppDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? LatestPublishedVersionId { get; set; }
    public string? LatestPublishedVersion { get; set; }
    public DateTimeOffset? LatestPublishedAt { get; set; }
    public string? LatestStatus { get; set; }
}