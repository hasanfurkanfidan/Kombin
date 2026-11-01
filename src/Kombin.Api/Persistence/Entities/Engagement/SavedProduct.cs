namespace Kombin.Api.Persistence.Entities.Engagement;

/// <summary>A bookmark. Not a <see cref="BaseEntity"/>: the pair (user, product) is the identity.</summary>
internal sealed class SavedProduct
{
    public Guid UserId { get; set; }

    public Guid ProductId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
