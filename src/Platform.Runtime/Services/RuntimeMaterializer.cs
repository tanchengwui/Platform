using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Platform.Runtime.Services;

public sealed class RuntimeMaterializer
{
    private readonly PackageCache _cache;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _contentRoot;

    public RuntimeMaterializer(PackageCache cache, IHttpClientFactory httpFactory, IConfiguration cfg)
    {
        _cache = cache;
        _httpFactory = httpFactory;

        // default: src\Platform.Runtime\Data\content (or Data\content in published output)
        _contentRoot = cfg.GetValue<string>("RuntimeContentRoot")
                       ?? Path.Combine(AppContext.BaseDirectory, "Data", "content");

        Directory.CreateDirectory(_contentRoot);
    }

    public sealed record ResolveActiveResponse(
        Guid AppId,
        Guid EnvId,
        Guid ActiveAppVersionId,
        Guid PackageId,
        string Sha256,
        string DownloadUrl
    );

    public async Task<(string staticPath, string appUrl)> PullAndMaterializeActiveAsync(Guid envId, Guid appId, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("PlatformApi");

        using var res = await http.PostAsJsonAsync("/runtime/resolve-active", new { envId, appId }, ct);
        res.EnsureSuccessStatusCode();

        var dto = await res.Content.ReadFromJsonAsync<ResolveActiveResponse>(cancellationToken: ct)
                  ?? throw new InvalidOperationException("resolve-active returned empty body.");

        var zipPath = await _cache.GetOrDownloadAsync(dto.PackageId, dto.DownloadUrl, dto.Sha256, ct);

        // Folder layout:
        // Data/content/<envId>/<appId>/<appVersionId>/
        // Data/content/<envId>/<appId>/current/  (atomic switch)
        var envDir = Path.Combine(_contentRoot, dto.EnvId.ToString("N"));
        var appDir = Path.Combine(envDir, dto.AppId.ToString("N"));
        Directory.CreateDirectory(appDir);

        var versionDir = Path.Combine(appDir, dto.ActiveAppVersionId.ToString("N"));
        if (Directory.Exists(versionDir)) Directory.Delete(versionDir, recursive: true);
        Directory.CreateDirectory(versionDir);

        // Generate HTML from package
        await GenerateHtmlFromPackageAsync(zipPath, versionDir, ct);

        // Write pointer metadata
        var currentMeta = new
        {
            dto.EnvId,
            dto.AppId,
            dto.ActiveAppVersionId,
            dto.PackageId,
            dto.Sha256,
            materializedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(versionDir, "current.json"),
            JsonSerializer.Serialize(currentMeta, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ct);

        // Atomic â€œcurrentâ€ swap (best-effort)
        var currentDir = Path.Combine(appDir, "current");
        var oldDir = Path.Combine(appDir, "current.old");

        try
        {
            if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
            if (Directory.Exists(currentDir)) Directory.Move(currentDir, oldDir);
            Directory.Move(versionDir, currentDir);
            if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
        }
        catch
        {
            // Fallback to copy
            CopyDirectory(versionDir, currentDir, overwrite: true);
        }

        var staticPath = $"/apps-static/{dto.EnvId:N}/{dto.AppId:N}/current/index.html";
        var appUrl = $"/apps/{dto.EnvId}/{dto.AppId}";
        return (staticPath, appUrl);
    }

    private static async Task GenerateHtmlFromPackageAsync(string zipPath, string outputDir, CancellationToken ct)
    {
        using var fs = File.OpenRead(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        // Read manifest for entry page key
        var manifest = ReadText(zip, "app.manifest.json");
        var entryKey = TryGetEntryPageKey(manifest) ?? "Home";

        // Build pages: UI_PAGE/<key>.json
        var pageEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("UI_PAGE/", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pageKeys = new List<string>();

        foreach (var e in pageEntries)
        {
            ct.ThrowIfCancellationRequested();

            var key = Path.GetFileNameWithoutExtension(e.FullName); // <key>.json
            pageKeys.Add(key);

            var json = ReadText(zip, e.FullName);
            var (title, bodyText) = ParseSimplePage(json);

            var html = BuildHtml(title ?? key, bodyText ?? "");
            await File.WriteAllTextAsync(Path.Combine(outputDir, $"{key}.html"), html, Encoding.UTF8, ct);
        }

        // index.html: redirect to entry page + show nav links
        var indexHtml = BuildIndexHtml(entryKey, pageKeys);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "index.html"), indexHtml, Encoding.UTF8, ct);
    }

    private static (string? title, string? bodyText) ParseSimplePage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? body = root.TryGetProperty("bodyText", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

            return (title, body);
        }
        catch
        {
            return (null, json);
        }
    }

    private static string BuildHtml(string title, string bodyText)
{
    var safeTitle = WebUtility.HtmlEncode(title);
    var safeBody = WebUtility.HtmlEncode(bodyText).Replace("\n", "<br/>");

    return $@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8""/>
  <meta name=""viewport"" content=""width=device-width,initial-scale=1""/>
  <title>{safeTitle}</title>
  <style>
    body {{ font-family: system-ui,Segoe UI,Arial; margin: 24px; line-height: 1.5; }}
    .card {{ max-width: 900px; padding: 18px 20px; border: 1px solid #ddd; border-radius: 12px; }}
    h1 {{ margin: 0 0 10px 0; font-size: 22px; }}
  </style>
</head>
<body>
  <div class=""card"">
    <h1>{safeTitle}</h1>
    <div>{safeBody}</div>
  </div>
</body>
</html>
";
}    private static string BuildIndexHtml(string entryKey, List<string> pageKeys)
    {
        var links = string.Join("\n", pageKeys.Select(k =>
            $"<li><a href=\"{WebUtility.HtmlEncode(k)}.html\">{WebUtility.HtmlEncode(k)}</a></li>"));

        return $"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8"/>
  <meta http-equiv="refresh" content="0; url={WebUtility.HtmlEncode(entryKey)}.html" />
  <meta name="viewport" content="width=device-width,initial-scale=1"/>
  <title>App</title>
</head>
<body>
  <p>Redirecting to <a href="{WebUtility.HtmlEncode(entryKey)}.html">{WebUtility.HtmlEncode(entryKey)}</a>...</p>
  <hr/>
  <h3>Pages</h3>
  <ul>
    {links}
  </ul>
</body>
</html>
""";
    }

    private static string? ReadText(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null) return null;
        using var s = e.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string? TryGetEntryPageKey(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.TryGetProperty("entryPageKey", out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        catch { }
        return null;
    }

    private static void CopyDirectory(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(f);
            File.Copy(f, Path.Combine(dst, name), overwrite);
        }
        foreach (var d in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(d);
            CopyDirectory(d, Path.Combine(dst, name), overwrite);
        }
    }
}


