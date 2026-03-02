namespace Platform.Api.Data.Studio;

public sealed class StudioEntity
{
    public Guid EntityId { get; set; }
    public Guid TenantId { get; set; }
    public Guid AppId { get; set; }

    // Display name in Studio
    public string Name { get; set; } = "";

    // Physical SQL table name (generated)
    public string TableName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<StudioEntityAttribute> Attributes { get; set; } = new();
}
