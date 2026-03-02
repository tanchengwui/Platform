using Platform.Api.Options;

// NOTE: This is a placeholder implementation intended for MinIO/S3.
// It is DISABLED unless Artifacts:Provider is set to "S3".
// To enable, add the MinIO .NET client package and replace this with real calls.
//
// Why placeholder? Keeps the solution buildable without extra NuGet deps.
// You can swap in AWS SDK (Amazon.S3) or Minio client later.

namespace Platform.Api.Storage;

public sealed class S3ArtifactStorage : IArtifactStorage
{
    private readonly ArtifactOptions _opt;

    public S3ArtifactStorage(ArtifactOptions opt) => _opt = opt;

    public Task WriteAsync(string key, Stream content, CancellationToken ct)
        => throw new NotSupportedException("S3ArtifactStorage not enabled in this build. Add an S3/MinIO client implementation.");

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
        => throw new NotSupportedException("S3ArtifactStorage not enabled in this build. Add an S3/MinIO client implementation.");

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
        => throw new NotSupportedException("S3ArtifactStorage not enabled in this build. Add an S3/MinIO client implementation.");

    public string GetContentType(string key)
        => key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip" : "application/octet-stream";
}
