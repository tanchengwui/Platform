namespace Platform.Api.Contracts;

public sealed class AppDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    // ServiceCenter deploy target
    public Guid? LatestPublishedVersionId { get; set; }
    public string? LatestPublishedVersion { get; set; }
    public DateTimeOffset? LatestPublishedAt { get; set; }

    // Optional: for your badges
    public string? LatestStatus { get; set; }
}