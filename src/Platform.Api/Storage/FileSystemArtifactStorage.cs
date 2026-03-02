using Platform.Api.Options;

namespace Platform.Api.Storage;

public sealed class FileSystemArtifactStorage : IArtifactStorage
{
    private readonly ArtifactOptions _opt;

    public FileSystemArtifactStorage(ArtifactOptions opt) => _opt = opt;

    private string ResolvePath(string keyOrPath)
    {
        // Backward compatible: if absolute path, use as-is.
        if (Path.IsPathRooted(keyOrPath))
            return keyOrPath;

        var root = _opt.RootPath ?? "./data/artifacts";
        return Path.Combine(root, keyOrPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, ct);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        Stream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(s);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        return Task.FromResult(File.Exists(path));
    }

    public string GetContentType(string key)
        => key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip" : "application/octet-stream";
}
