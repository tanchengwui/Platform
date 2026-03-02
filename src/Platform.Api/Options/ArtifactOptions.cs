namespace Platform.Api.Options;

public sealed class ArtifactOptions
{
    /// <summary>
    /// Storage provider name. Supported: "FileSystem" (default), "S3" (placeholder).
    /// </summary>
    public string Provider { get; set; } = "FileSystem";

    /// <summary>
    /// Root folder when Provider = FileSystem.
    /// </summary>
    public string RootPath { get; set; } = "./data/artifacts";

    // Optional S3/MinIO settings (used when Provider = S3)
    public string? S3Endpoint { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public string? S3Bucket { get; set; }
    public bool S3UseSsl { get; set; } = false;
}
