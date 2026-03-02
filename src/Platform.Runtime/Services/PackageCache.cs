using System.Security.Cryptography;

namespace Platform.Runtime.Services;

public sealed class PackageCache
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public PackageCache(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cacheDir = cfg.GetValue<string>("PackageCacheDir")
                    ?? Path.Combine(AppContext.BaseDirectory, "package-cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<string> GetOrDownloadAsync(Guid packageId, string downloadUrl, string sha256, CancellationToken ct)
    {
        var localPath = Path.Combine(_cacheDir, $"{packageId}.zip");
        var hashPath = localPath + ".sha256";

        // Cache hit: verify hash file
        if (File.Exists(localPath) && File.Exists(hashPath))
        {
            var existing = (await File.ReadAllTextAsync(hashPath, ct)).Trim();
            if (string.Equals(existing, sha256, StringComparison.OrdinalIgnoreCase))
                return localPath;
        }

        // Download
        using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var rs = await resp.Content.ReadAsStreamAsync(ct))
        {
            await rs.CopyToAsync(fs, ct);
        }

        // Verify SHA256
        var computed = ComputeSha256Hex(localPath);
        if (!string.Equals(computed, sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(localPath); } catch { /* ignore */ }
            throw new InvalidOperationException($"Package hash mismatch. Expected={sha256}, Computed={computed}");
        }

        await File.WriteAllTextAsync(hashPath, sha256, ct);
        return localPath;
    }

    private static string ComputeSha256Hex(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}