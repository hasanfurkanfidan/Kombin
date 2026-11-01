namespace Kombin.Api.Persistence.Entities;

internal abstract class BaseEntity : IAuditable
{
    /// <summary>Time-ordered GUID (v7) assigned on construction so inserts stay index-friendly.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
