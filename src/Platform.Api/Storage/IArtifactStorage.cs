using System.IO;

namespace Platform.Api.Storage;

public interface IArtifactStorage
{
    /// <summary>Writes bytes to a storage key (e.g. "packages/{id}.zip").</summary>
    Task WriteAsync(string key, Stream content, CancellationToken ct);

    /// <summary>Opens a readable stream for a storage key. Caller disposes.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>Best-effort content type for the key, used for HTTP downloads.</summary>
    string GetContentType(string key);
}
