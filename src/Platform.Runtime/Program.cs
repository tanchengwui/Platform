using Microsoft.Extensions.FileProviders;
using Platform.Runtime.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("PlatformApi", client =>
{
    // set in appsettings.json: PlatformApiBaseUrl = "http://localhost:5002"
    var baseUrl = builder.Configuration.GetValue<string>("PlatformApiBaseUrl") ?? "http://localhost:5002";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// --- Health checks ---
builder.Services.AddHealthChecks()
    .AddCheck<PlatformApiHealthCheck>("platform_api", tags: new[] { "ready" });

builder.Services.AddSingleton<RuntimeMaterializer>();

builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("PlatformApi");
    return new PackageCache(http, sp.GetRequiredService<IConfiguration>());

});

var app = builder.Build();

// Serve materialized HTML under /apps-static/*
var contentRoot = builder.Configuration.GetValue<string>("RuntimeContentRoot")
                 ?? Path.Combine(AppContext.BaseDirectory, "Data", "content");

Directory.CreateDirectory(contentRoot);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(contentRoot),
    RequestPath = "/apps-static"
});

// Human-friendly app URL: /apps/{envId}/{appId} -> redirect to current index
app.MapGet("/apps/{envId:guid}/{appId:guid}", (Guid envId, Guid appId) =>
{
    var url = $"/apps-static/{envId:N}/{appId:N}/current/index.html";
    return Results.Redirect(url);
});

// Internal sync: Runtime pulls active package and materializes it
app.MapPost("/sync/pull-active", async (
    Guid envId,
    Guid appId,
    HttpRequest req,
    RuntimeMaterializer mat,
    IConfiguration cfg,
    CancellationToken ct) =>
{
    // Optional shared key
    var key = cfg.GetValue<string>("InternalSyncKey");
    if (!string.IsNullOrWhiteSpace(key))
    {
        if (!req.Headers.TryGetValue("X-Platform-Internal-Key", out var got) || got != key)
            return Results.Unauthorized();
    }

    var (staticPath, appUrl) = await mat.PullAndMaterializeActiveAsync(envId, appId, ct);

    return Results.Ok(new
    {
        envId,
        appId,
        ok = true,
        staticPath,
        appUrl
    });
});



// Example endpoint you already have: /ui/{pageKey}?envId=...&appId=...

// Entry route: /ui?envId=...&appId=... (reads entryPageKey from app.manifest.json)
app.MapGet("/ui", async (
    Guid envId,
    Guid appId,
    IHttpClientFactory httpFactory,
    PackageCache cache,
    CancellationToken ct) =>
{
    var http = httpFactory.CreateClient("PlatformApi");

    var res = await http.PostAsJsonAsync("/runtime/resolve-active", new { envId, appId }, ct);
    if (!res.IsSuccessStatusCode)
        return Results.Problem("No active version resolved.", statusCode: (int)res.StatusCode);

    var dto = await res.Content.ReadFromJsonAsync<ResolveActiveResponse>(cancellationToken: ct);
    if (dto is null) return Results.Problem("Invalid resolve response.");

    var localZip = await cache.GetOrDownloadAsync(dto.PackageId, dto.DownloadUrl, dto.Sha256, ct);

    var manifestJson = ZipResourceReader.ReadText(localZip, "app.manifest.json");
    var entryPageKey = TryGetEntryPageKey(manifestJson) ?? "Home";

    var pageJson = ZipResourceReader.ReadText(localZip, $"UI_PAGE/{entryPageKey}.json");
    if (pageJson is null) return Results.NotFound();

    return Results.Text(pageJson, "application/json");
});

app.MapGet("/ui/{pageKey}", async (
    string pageKey,
    Guid envId,
    Guid appId,
    IHttpClientFactory httpFactory,
    PackageCache cache,
    CancellationToken ct) =>
{
    var http = httpFactory.CreateClient("PlatformApi");

    // call API resolve-active
    var res = await http.PostAsJsonAsync("/runtime/resolve-active",
        new { envId, appId }, ct);

    if (!res.IsSuccessStatusCode)
        return Results.Problem("No active version resolved.", statusCode: (int)res.StatusCode);

    var dto = await res.Content.ReadFromJsonAsync<ResolveActiveResponse>(cancellationToken: ct);
    if (dto is null) return Results.Problem("Invalid resolve response.");

    // download + cache package locally
    var localZip = await cache.GetOrDownloadAsync(dto.PackageId, dto.DownloadUrl, dto.Sha256, ct);

    // now read from zip (reuse your existing zip reader logic)
    var json = ZipResourceReader.ReadText(localZip, $"UI_PAGE/{pageKey}.json"); // implement using your current method
    if (json is null) return Results.NotFound();

    return Results.Text(json, "application/json");
});

// Home (for humans)
app.MapGet("/", () => Results.Text(
    "Platform.Runtime is running. Try: /ui/{pageKey}?envId=...&appId=...  |  Health: /health/live , /health/ready",
    "text/plain"));

// Health endpoints (for ops)
app.MapGet("/health/live", () => Results.Ok(new { status = "Live" }));

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready")
});


static string? TryGetEntryPageKey(string? manifestJson)
{
    if (string.IsNullOrWhiteSpace(manifestJson)) return null;

    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(manifestJson);
        if (doc.RootElement.TryGetProperty("entryPageKey", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
            return p.GetString();
    }
    catch { /* ignore */ }

    return null;
}

app.Run();

file sealed record ResolveActiveResponse(
    Guid AppId,
    Guid EnvId,
    Guid ActiveAppVersionId,
    Guid PackageId,
    string Sha256,
    string DownloadUrl
);
