namespace Platform.Api.Data.Studio;

public sealed class StudioEntityAttribute
{
    public Guid AttributeId { get; set; }
    public Guid EntityId { get; set; }

    public string Name { get; set; } = "";
    public string Type { get; set; } = "String"; // Guid, String, Int32, Boolean, DateTimeOffset, Decimal
    public int? Length { get; set; } // for String (nvarchar)
    public bool Required { get; set; }

    // for future: default values, indexes, references, etc.
    public DateTimeOffset CreatedAt { get; set; }

    public StudioEntity? Entity { get; set; }
}
